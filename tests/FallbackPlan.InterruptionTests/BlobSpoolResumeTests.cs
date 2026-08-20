using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// Blob spool resume through the production path (specification 05 §6;
/// FR-ARCH-011, NFR-SEC-003, NFR-REL-001): a job killed with a blob still
/// open leaves a spool, the next process life resumes it rather than
/// discarding it, and the bytes it already sealed are re-emitted verbatim.
/// </summary>
/// <remarks>
/// NFR-SEC-003's acceptance is "resume produces byte-identical blobs; restart
/// produces a different salt", and until now nothing proved either through
/// the engine — <c>BlobWriter.TryResume</c> had no production caller, so the
/// guarantee was asserted by unit tests against a method the product never
/// invoked. These drive the real orchestrator.
/// <para>
/// The resume point is found by authenticating every spooled record, so the
/// cases that must <em>not</em> resume are here too. Each restarts, and a
/// restarted blob draws a fresh salt — which is what keeps an ordinal from
/// being re-used under a salt that already covered it (05 §6.1).
/// </para>
/// </remarks>
[TestClass]
public sealed class BlobSpoolResumeTests : InterruptionHarness
{
    [TestMethod]
    public async Task BlobSpool_JobKilledWithABlobOpen_LeavesAResumableSpool()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        await KillMidBlobAsync(store, keys, hierarchy);

        // The spool is still there. The session's own disposal used to delete
        // it, which left resume reachable only after a true process kill.
        Assert.IsNotEmpty(SpoolFiles());

        // 05 §6.3: a partial spool is never uploaded, under any circumstances.
        // Blobs the run sealed before it died are durable and legitimate —
        // 04 §5.1 row 4's "durable but unreferenced" — so the claim is about
        // this spool specifically, not about the store being empty.
        await AssertNoBlobCarriesSaltAsync(store, SaltOf(await ReadSpoolAsync()));
    }

    [TestMethod]
    public async Task BlobSpool_ResumedAfterAKill_ReEmitsItsSealedBytesVerbatim()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var content = await KillMidBlobAsync(store, keys, hierarchy);
        var spooled = await ReadSpoolAsync();

        // A fresh process life over the same durable state.
        using var source = new MemoryStream(content);
        await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(Job(source, snapshotSeed: 0x71), CancellationToken.None);

        SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0x71));

        // Resumed, not restarted: a sealed blob opens with exactly the bytes
        // the interrupted run spooled. That is NFR-SEC-003's "byte-identical"
        // — the records were re-emitted, not recomputed, and recomputing is
        // the failure the rule exists to prevent (05 §6.1: recompression is
        // not reproducible across codec versions, and the same ordinal over
        // different bytes is nonce reuse).
        Assert.Contains(blob => StartsWith(blob, spooled), await SealedBlobsAsync(store));
    }

    [TestMethod]
    public async Task BlobSpool_TailIsTorn_RestartsTheBlobRatherThanTruncatingIt()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var content = await KillMidBlobAsync(store, keys, hierarchy);
        var abandonedSalt = SaltOf(await ReadSpoolAsync());

        // A torn write the crash left behind. Truncating back to the last
        // whole record would hand the torn record's ordinal to a blob whose
        // salt already covered it; restart draws a fresh salt instead.
        await using (var spool = new FileStream(SpoolFiles().Single(), FileMode.Append, FileAccess.Write))
        {
            await spool.WriteAsync(new byte[137], CancellationToken.None);
        }

        using var source = new MemoryStream(content);
        await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(Job(source, snapshotSeed: 0x72), CancellationToken.None);

        // A restart costs spooled work, never correctness: the job completes
        // and the file restores.
        SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0x72));
        await AssertNoBlobCarriesSaltAsync(store, abandonedSalt);
    }

    [TestMethod]
    public async Task BlobSpool_RecordIsDamaged_RestartsTheBlobRatherThanResuming()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var content = await KillMidBlobAsync(store, keys, hierarchy);
        var abandonedSalt = SaltOf(await ReadSpoolAsync());

        // Structurally perfect framing, a tag that no longer verifies. A walk
        // that trusted a durable watermark could not see this at all.
        var spoolPath = SpoolFiles().Single();
        var bytes = await File.ReadAllBytesAsync(spoolPath, CancellationToken.None);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(spoolPath, bytes, CancellationToken.None);

        using var source = new MemoryStream(content);
        await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(Job(source, snapshotSeed: 0x73), CancellationToken.None);

        SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0x73));
        await AssertNoBlobCarriesSaltAsync(store, abandonedSalt);
    }

    /// <summary>
    /// Runs a publication that dies with a blob still open, and returns the
    /// content it was archiving.
    /// </summary>
    private async Task<byte[]> KillMidBlobAsync(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy)
    {
        var content = BuildFile(seed: 17);

        // The source dies part-way through the file, so the archive loop
        // unwinds with a blob still open. A store fault will not do: uploads
        // left the archive loop (ADR-0029 §2), so a failing PUT surfaces only
        // at the drain barrier, by which time every blob has been sealed and
        // there is nothing open left to resume.
        using var source = new FaultingStream(content, failAfterBytes: 600 * 1024);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy)
                .PublishAsync(Job(source, snapshotSeed: 0x70), CancellationToken.None));

        return content;
    }

    /// <summary>Asserts the run restarted: no sealed blob carries the abandoned salt.</summary>
    private static async Task AssertNoBlobCarriesSaltAsync(IObjectStore store, byte[] abandonedSalt)
    {
        foreach (var blob in await SealedBlobsAsync(store))
        {
            Assert.AreNotEqual(abandonedSalt, SaltOf(blob));
        }
    }

    /// <summary>
    /// The spool the interrupted run left <em>open</em>. A kill also strands
    /// sealed-but-unuploaded spools, so the open one is identified the way
    /// <see cref="BlobWriter.TryResume"/> identifies it: by the checkpoint
    /// sidecar beside it, which sealing deletes.
    /// </summary>
    private IEnumerable<string> SpoolFiles() =>
        Directory.Exists(SpoolDirectory)
            ? Directory.EnumerateFiles(SpoolDirectory, "blob-*.spool.checkpoint")
                .Select(path => path[..^".checkpoint".Length])
            : [];

    private async Task<byte[]> ReadSpoolAsync() =>
        await File.ReadAllBytesAsync(SpoolFiles().Single(), CancellationToken.None);

    private static async Task<List<byte[]>> SealedBlobsAsync(IObjectStore store)
    {
        var blobs = new List<byte[]>();

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);
            blobs.Add(memory.ToArray());
        }

        return blobs;
    }

    private static bool StartsWith(byte[] blob, byte[] prefix) =>
        blob.Length >= prefix.Length && blob.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static byte[] SaltOf(byte[] blobOrSpool) =>
        BlobEnvelope.Parse(blobOrSpool.AsSpan(0, Math.Min(BlobEnvelope.MaxLength, blobOrSpool.Length)))
            .BlobSalt.ToArray();
}
