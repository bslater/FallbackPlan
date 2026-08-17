using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The periodic half of destination verification: every blob at a replica,
/// eventually, re-read and checked against the digest sealed into its own
/// footer. The range challenge that runs at sync time proves possession at the
/// moment it asks and samples a handful of objects; between syncs, and across
/// everything it did not sample, this is what notices rot.
/// </summary>
[TestClass]
public sealed class ReplicaSweepTests : ArchiveTestHarness
{
    private string ReplicaRoot => Path.Combine(StoreRoot, "..", "replica");

    /// <summary>Archives a test file into a store and mirrors it to a second one.</summary>
    private async Task<(LocalFileSystemObjectStore Source, LocalFileSystemObjectStore Replica, RepositoryKeySet Keys)>
        SeedAsync()
    {
        var store = CreateStore();
        var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(BuildTestFile());
        await archiver.ArchiveAsync(source, CancellationToken.None);

        Directory.CreateDirectory(ReplicaRoot);
        var replica = new LocalFileSystemObjectStore(ReplicaRoot);
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            var bytes = new byte[entry.Length];
            await read.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            await replica.PutAsync(
                entry.Key,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }

        return (store, replica, keys);
    }

    private static async Task<List<ObjectKey>> BlobKeysAsync(LocalFileSystemObjectStore store)
    {
        var keys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key);
        }

        return keys;
    }

    [TestMethod]
    public async Task Sweep_AnIntactReplica_FindsNothingAndClosesTheCircuit()
    {
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(result.Findings);
        Assert.IsTrue(result.Examined > 1, "the small-blob policy must produce several blobs to sweep");
        Assert.IsTrue(result.CompletedCircuit);
        Assert.IsNull(result.NextCursor, "a closed circuit starts over rather than resuming");
    }

    [TestMethod]
    public async Task Sweep_AByteFlippedInAReplicaBlob_IsFound()
    {
        // What the sealed digest is for. Nothing else in the system reads
        // these bytes between syncs, so silent rot here is invisible until a
        // restore needs them — which is the worst possible moment to find out.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var victim = (await BlobKeysAsync(replica))[0];
        var path = Path.Combine(ReplicaRoot, victim.Value.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.HasCount(1, result.Findings);
        Assert.Contains(victim.Value, result.Findings[0], StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_AValidBlobStoredUnderAnothersKey_IsCaughtByTheLengthComparison()
    {
        // Why the digest alone is not enough, demonstrated rather than
        // asserted. A replica holding a perfectly well-formed, correctly
        // sealed blob under the WRONG key passes FooterAndDigest completely —
        // the reader authenticates the bytes against their own footer and does
        // not bind the store key to the envelope's blob id. Nothing internal to
        // that object is wrong; it is simply not the object we put there.
        // Comparing lengths against the source is what notices.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var intact = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);
        Assert.IsEmpty(intact.Findings, "the replica must start intact");

        var lengths = (await BlobKeysAsync(replica))
            .Select(key => (Key: key, Length: new FileInfo(
                Path.Combine(ReplicaRoot, key.Value.Replace('/', Path.DirectorySeparatorChar))).Length))
            .ToList();
        var victim = lengths[0].Key;
        var other = lengths.OrderByDescending(entry => Math.Abs(entry.Length - lengths[0].Length)).First().Key;
        var otherPath = Path.Combine(ReplicaRoot, other.Value.Replace('/', Path.DirectorySeparatorChar));
        var victimPath = Path.Combine(ReplicaRoot, victim.Value.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreNotEqual(
            new FileInfo(otherPath).Length, new FileInfo(victimPath).Length,
            "the two blobs must differ in length for this to reach the length check");
        await File.WriteAllBytesAsync(victimPath, await File.ReadAllBytesAsync(otherPath));

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsNotEmpty(result.Findings);
        Assert.Contains(
            "where the source holds", string.Join(" ", result.Findings), StringComparison.Ordinal,
            "the length comparison must be what catches this, not the digest");
    }

    [TestMethod]
    public async Task Sweep_ABudgetSmallerThanTheArchive_ResumesAndEventuallyCoversEverything()
    {
        // The property that makes a bounded segment worth anything: run it
        // enough times and every blob has been read. Without the cursor the
        // same first N objects would be swept forever.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(all.Count >= 3, "the archive must span several blobs for segmenting to mean anything");

        var swept = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var circuits = 0;

        for (var segment = 0; segment < 50 && circuits == 0; segment++)
        {
            var result = await ReplicaSweep.RunAsync(
                Repo, keys, replica, source, cursor, budget: 1, CancellationToken.None);

            // One blob per segment, and never the same one twice.
            if (result.NextCursor is { } next)
            {
                Assert.IsTrue(swept.Add(next), $"'{next}' was swept twice — the cursor did not advance");
            }

            cursor = result.NextCursor;
            if (result.CompletedCircuit)
            {
                circuits++;
            }
        }

        Assert.AreEqual(1, circuits, "the sweep must close its circuit rather than running forever");
        Assert.IsTrue(
            swept.IsSubsetOf(all) && swept.Count >= all.Count - 1,
            $"every blob must be reached across segments: swept {swept.Count} of {all.Count}");
    }

    [TestMethod]
    public async Task Sweep_ACursorNamingASinceTrimmedBlob_ResumesAtTheNextKeyRatherThanFailing()
    {
        // Trim deletes historic blobs from staging and, under a retention
        // policy, from replicas. A cursor is only ever compared against, never
        // looked up, so the key it names may be gone by the next segment.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var vanished = all[0];
        await replica.DeleteAsync(ObjectKey.Parse(vanished), DeleteConditions.None, CancellationToken.None);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: vanished, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(result.Findings);
        Assert.AreEqual(all.Count - 1, result.Examined, "everything ordinally after the vanished cursor");
    }

    [TestMethod]
    public async Task Sweep_AnEmptyReplica_ClosesTheCircuitRatherThanReportingDamage()
    {
        // There is nothing to disprove. The shortfall check (a destination
        // that lost everything) is fan-out's job and has its own signal — the
        // sweep must not double-report it as corruption.
        var (source, _, keys) = await SeedAsync();
        using var held = keys;
        Directory.CreateDirectory(Path.Combine(ReplicaRoot, "empty"));
        var empty = new LocalFileSystemObjectStore(Path.Combine(ReplicaRoot, "empty"));

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, empty, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.AreEqual(0, result.Examined);
        Assert.IsEmpty(result.Findings);
        Assert.IsTrue(result.CompletedCircuit);
    }
}
