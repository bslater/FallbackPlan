using FallbackPlan.Repository;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The writer's sequence file regresses — the state a power loss leaves when
/// <c>sequence.txt</c> was replaced but the replacement never reached the
/// platter. Every number the previous run consumed is then handed out again:
/// the write intent's journal key, the blob counters, and the delta sequence
/// all collide with objects the store already holds (specification 08 §2;
/// architecture 04 §2 classifies the result as identity cloning).
/// </summary>
/// <remarks>
/// The store is immutable under a live key, so the colliding puts do not
/// corrupt the earlier run — they are answered <c>AlreadyExists</c> and the
/// earlier bytes survive (05 §5.1). The danger is the writer treating that
/// answer as durability for <em>its</em> bytes: it would then publish an
/// index delta and a snapshot describing records the store never received.
/// A writer that cannot establish upload success may only re-put
/// byte-identical content (05 §5.1); anything else must refuse, and these
/// hold that the refusal happens while the source data still exists.
/// </remarks>
[TestClass]
public sealed class SequenceRollbackTests : InterruptionHarness
{
    private string SequencePath => Path.Combine(SpoolDirectory, "sequence.txt");

    [TestMethod]
    public async Task Publication_TheSequenceFileRegressed_RefusesRatherThanRepublishingUnderUsedNumbers()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var first = BuildFile(seed: 1);
        using (var source = new MemoryStream(first))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // The state a power loss preserves: the file as it was before the
        // second run's replacement reached the disk.
        var preSecondRun = await File.ReadAllBytesAsync(SequencePath);

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await File.WriteAllBytesAsync(SequencePath, preSecondRun);

        // The regressed writer's first allocation is the write intent, whose
        // journal key the second run already used. The store still holds the
        // second run's record there, so treating the put as success would
        // mean starting a publication whose intent was never written.
        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None));
        }

        // The refusal must come before anything is published: no third
        // snapshot, and both committed snapshots still restore.
        Assert.AreEqual(2, CountUnder("snapshots"));
        SequenceAssert.AreEqual(first, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
    }

    [TestMethod]
    public async Task Publication_TheSequenceRegressedAndTheJournalWasPruned_RefusesAtTheBlobUpload()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var first = BuildFile(seed: 1);
        using (var source = new MemoryStream(first))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var preSecondRun = await File.ReadAllBytesAsync(SequencePath);

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await File.WriteAllBytesAsync(SequencePath, preSecondRun);

        // A future collector prunes retired journal records (07 §7 gives
        // deltas the same lifecycle), so the journal collision cannot be the
        // only tripwire. With the journal keys free again, the intent put
        // succeeds and the first collision is the blob itself: same writer,
        // same counter, a different salt — different bytes under a key the
        // store already holds.
        Directory.Delete(Path.Combine(StoreRoot, "journal"), recursive: true);

        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None));
        }

        Assert.AreEqual(2, CountUnder("snapshots"));
        SequenceAssert.AreEqual(first, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
    }

}
