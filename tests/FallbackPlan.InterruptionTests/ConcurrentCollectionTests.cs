using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// Exit criterion 10 (specification 08 §8; FR-GC-001): a collector running
/// mid-backup deletes nothing — every blob covered by an unretired,
/// unexpired intent is reachable, no exceptions, no heuristics — and an
/// unparseable intent forces the conservative reading.
/// </summary>
[TestClass]
public sealed class ConcurrentCollectionTests : InterruptionHarness
{
    [TestMethod]
    public async Task GarbageCollection_RunsWhileABackupIsInFlight_DeletesNothing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // Interrupt after upload: blobs durable, nothing references them —
        // to a reachability walk they are indistinguishable from garbage,
        // and only the intent stands between them and deletion (08 §1).
        using (var source = new MemoryStream(BuildFile(seed: 7)))
        {
            await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy, new KillAfter(PublicationStep.UploadBlobs))
                    .PublishAsync(Job(source, snapshotSeed: 0xE5), CancellationToken.None));
        }

        Assert.IsTrue(CountUnder("blobs") > 0);

        var wouldDelete = await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000);

        Assert.IsEmpty(wouldDelete);
    }

    [TestMethod]
    public async Task GarbageCollection_AnIntentCannotBeParsed_ProtectsEverythingItMightCover()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        using (var source = new MemoryStream(BuildFile(seed: 7)))
        {
            await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy, new KillAfter(PublicationStep.UploadBlobs))
                    .PublishAsync(Job(source, snapshotSeed: 0xE5), CancellationToken.None));
        }

        // Corrupt every journal record: the collector can no longer see WHICH
        // blobs are covered — 08 §8's rule makes everything reachable,
        // because failing to collect wastes space and collecting wrongly
        // loses data.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(StoreRoot, "journal"), "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            bytes[^1] ^= 0x01;
            await File.WriteAllBytesAsync(file, bytes);
        }

        var wouldDelete = await SimulateCollectorMarkAsync(store, hierarchy, 0, 1_722_600_000_000);

        Assert.IsEmpty(wouldDelete);
    }

    [TestMethod]
    public async Task GarbageCollection_IntentRetired_ReleasesCoverageOnlyOnceTheSnapshotExists()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // A completed publication retires its intent — its blobs are now
        // reachable through the snapshot, which this simulated collector
        // does not walk; they show as candidates HERE precisely because
        // retirement released the journal's protection at the right moment
        // (after the snapshot became durable, 08 §5).
        using (var source = new MemoryStream(BuildFile(seed: 7)))
        {
            await CreateOrchestrator(store, keys, hierarchy)
                .PublishAsync(Job(source, snapshotSeed: 0xE6), CancellationToken.None);
        }

        var candidates = await SimulateCollectorMarkAsync(store, hierarchy, 0, 1_722_600_000_000);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(1, CountUnder("snapshots"));
    }
}
