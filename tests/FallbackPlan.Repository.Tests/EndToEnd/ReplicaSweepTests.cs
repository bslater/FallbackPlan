using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

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

    private static string PathOf(string root, ObjectKey key) =>
        Path.Combine(root, key.Value.Replace('/', Path.DirectorySeparatorChar));

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
    public async Task Sweep_AFinding_NamesTheKeyItFoundDamaged()
    {
        // The finding's prose is for a person; the key is for whatever acts
        // on it. Repairing an object must not mean parsing a sentence back.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var victim = (await BlobKeysAsync(replica))[0];
        var path = Path.Combine(ReplicaRoot, victim.Value.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.AreEqual(victim.Value, Assert.ContainsSingle(result.DamagedKeys));
    }

    [TestMethod]
    public async Task Sweep_AByteBudget_EndsTheSegmentAtTheBlobThatSpendsIt_AndTheNextResumesAfterIt()
    {
        // A segment is bounded by bytes as well as by count, because the one
        // transfer worker is held for as long as the segment reads: sixty-four
        // blobs is seconds off a local disk and an hour through a slow limit.
        // The bound never stops a segment reading at least one blob, or a blob
        // larger than the budget would park the circuit on itself for ever.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        Assert.IsGreaterThanOrEqualTo(3, all.Count, "the archive must span several blobs for a byte bound to bite");

        var first = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, byteBudget: 1, CancellationToken.None);

        Assert.AreEqual(1, first.Examined, "a budget smaller than one blob still reads one");
        Assert.AreEqual(all[0], first.NextCursor);
        Assert.IsFalse(first.CompletedCircuit);

        var whole = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: first.NextCursor, budget: 1_000, byteBudget: long.MaxValue,
            CancellationToken.None);

        Assert.AreEqual(all.Count - 1, whole.Examined, "the next segment resumes after the blob the first one read");
        Assert.IsTrue(whole.CompletedCircuit);
    }

    [TestMethod]
    public async Task Sweep_ABlobThatWillNotRead_StopsTheSegmentAtIt_KeepingWhatWasReadBeforeIt()
    {
        // A read that fails is not a finding: a disk that has gone from under
        // the segment fails every read the same way one bad sector fails one,
        // and only another attempt tells them apart. So the segment stops at
        // the blob, says which and why, and keeps what it read before it — a
        // retry then begins at that blob rather than re-reading the run up to
        // it, which on every pass would be the whole segment again.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        Assert.IsGreaterThanOrEqualTo(3, all.Count, "the archive must span several blobs");
        var failing = new UnreadableObjectStore(replica, key => key == all[1]);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, failing, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.AreEqual(all[1], result.StalledOn);
        Assert.Contains(UnreadableObjectStore.Refusal, result.Stall!, StringComparison.Ordinal);
        Assert.AreEqual(1, result.Examined, "the blob before it was read, and stands");
        Assert.AreEqual(all[0], result.NextCursor, "the next attempt begins at the blob that would not read");
        Assert.IsFalse(result.CompletedCircuit);
        Assert.IsEmpty(result.Findings, "a blob that would not be read has not been shown to be altered");
        Assert.IsEmpty(result.DamagedKeys);
    }

    [TestMethod]
    public async Task Sweep_ABlobTheStoreHoldsOnlyInAnArchivalTier_IsPassedOver_AndTheCircuitCloses()
    {
        // An archived blob reads no differently on the next attempt: its
        // store serves nothing of it until a person rehydrates it (ADR-0104).
        // Stopping there would stop the circuit there for good, and nothing
        // after it would ever be re-read. So it is passed over, named, and
        // neither read nor shown to be altered.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        Assert.IsGreaterThanOrEqualTo(3, all.Count, "the archive must span several blobs");
        var cold = new ArchivingObjectStore(replica, key => key == all[1]);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, cold, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsNull(result.StalledOn);
        Assert.IsNull(result.Stall);
        Assert.IsTrue(result.CompletedCircuit, "the circuit reaches the end past the archived blob");
        CollectionAssert.AreEqual(new[] { all[1] }, result.ArchivedKeys.ToArray());
        Assert.AreEqual(all.Count - 1, result.Examined, "an archived blob is not read");
        Assert.DoesNotContain(all[1], result.ExaminedKeys);
        Assert.IsEmpty(result.Findings);
        Assert.IsEmpty(result.DamagedKeys, "nothing archived is repaired");
    }

    [TestMethod]
    public async Task Sweep_ASegmentEndingAtAnArchivedBlob_ResumesAfterIt()
    {
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        Assert.IsGreaterThanOrEqualTo(3, all.Count, "the archive must span several blobs");
        var cold = new ArchivingObjectStore(replica, key => key == all[1]);

        var first = await ReplicaSweep.RunAsync(
            Repo, keys, cold, source, cursor: null, budget: 2, CancellationToken.None);

        Assert.IsFalse(first.CompletedCircuit);
        Assert.AreEqual(all[1], first.NextCursor, "the next segment does not meet it again");

        var next = await ReplicaSweep.RunAsync(
            Repo, keys, cold, source, cursor: first.NextCursor, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(next.ArchivedKeys);
        Assert.AreEqual(all.Count - 2, next.Examined);
        Assert.IsTrue(next.CompletedCircuit);
    }

    [TestMethod]
    public async Task Sweep_AnArchivedBlob_SpendsNoneOfTheSegmentsByteBudget()
    {
        // Nothing of it is read, so a segment bounded in bytes reads as much
        // of what is readable as it would with the archived blob gone.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var lengths = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await foreach (var entry in replica.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            lengths[entry.Key.Value] = entry.Length;
        }

        Assert.IsGreaterThanOrEqualTo(3, lengths.Count, "the archive must span several blobs");
        var coldKey = lengths.Keys.First();
        var cold = new ArchivingObjectStore(replica, key => key == coldKey);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, cold, source, cursor: null, budget: 1_000,
            byteBudget: lengths.Where(pair => pair.Key != coldKey).Sum(pair => pair.Value), CancellationToken.None);

        Assert.AreEqual(lengths.Count - 1, result.Examined);
        Assert.IsTrue(result.CompletedCircuit);
    }

    [TestMethod]
    public async Task Sweep_ASegmentThatPassesOverAnArchivedBlobAndThenStalls_NamesIt_AndResumesPastIt()
    {
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        Assert.IsGreaterThanOrEqualTo(3, all.Count, "the archive must span several blobs");
        var cold = new ArchivingObjectStore(new UnreadableObjectStore(replica, key => key == all[1]), key => key == all[0]);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, cold, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.AreEqual(all[1], result.StalledOn);
        CollectionAssert.AreEqual(new[] { all[0] }, result.ArchivedKeys.ToArray());
        Assert.AreEqual(0, result.Examined);
        Assert.AreEqual(all[0], result.NextCursor, "the next attempt begins at the blob that would not read");
    }

    [TestMethod]
    public async Task Sweep_TheFirstBlobWillNotRead_LeavesTheCursorWhereItWas()
    {
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var all = (await BlobKeysAsync(replica)).Select(key => key.Value).Order(StringComparer.Ordinal).ToList();
        var failing = new UnreadableObjectStore(replica, key => key == all[1]);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, failing, source, cursor: all[0], budget: 1_000, CancellationToken.None);

        Assert.AreEqual(all[1], result.StalledOn);
        Assert.AreEqual(0, result.Examined);
        Assert.AreEqual(all[0], result.NextCursor, "nothing was read, so nothing moves");
        Assert.IsFalse(result.CompletedCircuit, "a segment that stopped short has not reached the end");
    }

    [TestMethod]
    public async Task Sweep_ASegmentThatReadsEverything_HasNoStall()
    {
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsNull(result.StalledOn);
        Assert.IsNull(result.Stall);
    }

    [TestMethod]
    public async Task Sweep_AValidBlobStoredUnderAnothersKey_IsFoundByItsKey_WithNoSourceToCompare()
    {
        // Why the digest alone is not enough, demonstrated rather than
        // asserted. A store holding a perfectly well-formed, correctly sealed
        // blob under the WRONG key passes FooterAndDigest completely: the
        // bytes authenticate against their own footer. Nothing internal to
        // that object is wrong; it is simply not the object its key names,
        // and its envelope says so. The staging archive is swept with no
        // source beside it, so the envelope is the only witness it has.
        var (_, replica, keys) = await SeedAsync();
        using var held = keys;

        var lengths = (await BlobKeysAsync(replica))
            .Select(key => (Key: key, Length: new FileInfo(PathOf(ReplicaRoot, key)).Length))
            .ToList();
        var victim = lengths[0].Key;
        var other = lengths[1].Key;
        await File.WriteAllBytesAsync(PathOf(ReplicaRoot, victim), await File.ReadAllBytesAsync(PathOf(ReplicaRoot, other)));

        using (var verifier = new VerifyEngine(Repo, keys, replica))
        {
            var unbound = await verifier.VerifyBlobAsync(
                victim, new FileInfo(PathOf(ReplicaRoot, victim)).Length, VerifyLevel.FooterAndDigest,
                CancellationToken.None);
            Assert.IsTrue(unbound.Ok, "the digest must pass the swap, or this shows nothing about the key");
        }

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source: null, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.AreEqual(victim.Value, Assert.ContainsSingle(result.DamagedKeys));
        Assert.Contains("a different blob", Assert.ContainsSingle(result.Findings), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_ALengthThatDiffersBecauseTheSourcesCopyIsDamaged_IsTheSourcesFinding_NotTheReplicas()
    {
        // The replica's blob is the one its key names, unaltered — its key
        // and its digest say so — so a source holding another length under
        // that key holds something else. Blaming the replica would repair a
        // sound object, and leave the damaged one unsaid.
        var (source, replica, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        var cut = await File.ReadAllBytesAsync(PathOf(StoreRoot, victim));
        await File.WriteAllBytesAsync(PathOf(StoreRoot, victim), cut[..^100]);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(result.Findings, string.Join(" | ", result.Findings));
        Assert.IsEmpty(result.DamagedKeys);
        Assert.AreEqual(victim.Value, Assert.ContainsSingle(result.SourceDamagedKeys));
        Assert.Contains(victim.Value, Assert.ContainsSingle(result.SourceFindings), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_ALengthThatDiffersWhereTheSourcesCopyWillNotRead_IsNobodysFinding()
    {
        // A copy that will not be read has not been shown to be altered, and
        // the replica's has been shown not to be.
        var (_, replica, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        var cut = await File.ReadAllBytesAsync(PathOf(StoreRoot, victim));
        await File.WriteAllBytesAsync(PathOf(StoreRoot, victim), cut[..^100]);
        var source = new UnreadableObjectStore(CreateStore(), key => key == victim.Value);

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(result.Findings, string.Join(" | ", result.Findings));
        Assert.IsEmpty(result.DamagedKeys);
        Assert.IsEmpty(result.SourceDamagedKeys);
        Assert.IsNull(result.StalledOn, "the replica's own copy read whole");
    }

    [TestMethod]
    public async Task Sweep_ALengthThatDiffersWhereBothCopiesProveSound_IsNobodysFinding()
    {
        // Two blobs written under one identity, which only a writer whose
        // counter went back can produce: each is the blob its key names,
        // unaltered since it was sealed. Neither is damage, and replacing
        // either from the other would lose the one replaced, so the sweep
        // condemns neither.
        var keys = CreateKeys();
        using var held = keys;
        var source = CreateStore();
        using (var file = new MemoryStream(BuildTestFile()))
        {
            await CreateArchiver(source, keys).ArchiveAsync(file, CancellationToken.None);
        }

        Directory.CreateDirectory(ReplicaRoot);
        var replica = new LocalFileSystemObjectStore(ReplicaRoot);
        var noise = new byte[BuildTestFile().Length];
        new Random(7).NextBytes(noise);
        using (var file = new MemoryStream(noise))
        {
            await CreateArchiver(replica, keys).ArchiveAsync(file, CancellationToken.None);
        }

        var differing = (await BlobKeysAsync(replica))
            .Where(key => File.Exists(PathOf(StoreRoot, key))
                && new FileInfo(PathOf(StoreRoot, key)).Length != new FileInfo(PathOf(ReplicaRoot, key)).Length)
            .ToList();
        Assert.IsNotEmpty(differing, "the two archives must share a blob identity at different lengths");

        var result = await ReplicaSweep.RunAsync(
            Repo, keys, replica, source, cursor: null, budget: 1_000, CancellationToken.None);

        Assert.IsEmpty(result.Findings, string.Join(" | ", result.Findings));
        Assert.IsEmpty(result.DamagedKeys);
        Assert.IsEmpty(result.SourceDamagedKeys);
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
