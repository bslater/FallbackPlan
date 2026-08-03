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
public sealed class ConcurrentCollectionTests : InterruptionHarness
{
    /// <summary>
    /// The 08 §8 collector obligations as code: enumerate the journal before
    /// marking, protect intent-covered blobs, treat the unparseable as live.
    /// Returns what it WOULD delete — the tests assert emptiness.
    /// </summary>
    private static async Task<List<ObjectKey>> SimulateCollectorMarkAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        Repository.Crypto.KeyHierarchy hierarchy,
        ulong currentGeneration,
        ulong nowMs)
    {
        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync((uint)currentGeneration, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, currentGeneration, nowMs, skewMarginMs: 60_000);

        // Reachability, phase-0 shape: a blob referenced by an index delta or
        // covered by a live intent is reachable; everything else is a
        // candidate. The candidates here are computed against intents only —
        // the scenario deletes the index plane's contribution deliberately.
        var candidates = new List<ObjectKey>();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            // The collector cannot map a store key back to a blob id (that
            // is the point of keyed store keys) — coverage is checked by
            // opening the envelope, exactly as a real collector must.
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, Repository.Packing.BlobEnvelope.Length), CancellationToken.None);
            var envelopeBytes = new byte[Repository.Packing.BlobEnvelope.Length];
            await read.Content!.ReadExactlyAsync(envelopeBytes);
            var envelope = Repository.Packing.BlobEnvelope.Parse(envelopeBytes);

            if (!survey.IsCovered(envelope.BlobId))
            {
                candidates.Add(entry.Key);
            }
        }

        return candidates;
    }

    [Fact]
    public async Task A_collector_running_mid_backup_deletes_nothing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // Interrupt after upload: blobs durable, nothing references them —
        // to a reachability walk they are indistinguishable from garbage,
        // and only the intent stands between them and deletion (08 §1).
        using (var source = new MemoryStream(BuildFile(seed: 7)))
        {
            await Assert.ThrowsAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy, new KillAfter(PublicationStep.UploadBlobs))
                    .PublishAsync(Job(source, snapshotSeed: 0xE5), CancellationToken.None));
        }

        Assert.True(CountUnder("blobs") > 0);

        var wouldDelete = await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000);

        Assert.Empty(wouldDelete);
    }

    [Fact]
    public async Task An_unparseable_intent_protects_everything()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        using (var source = new MemoryStream(BuildFile(seed: 7)))
        {
            await Assert.ThrowsAsync<PublicationKilledException>(async () =>
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

        Assert.Empty(wouldDelete);
    }

    [Fact]
    public async Task A_retired_intent_releases_coverage_only_after_the_snapshot_exists()
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

        Assert.NotEmpty(candidates);
        Assert.Equal(1, CountUnder("snapshots"));
    }
}
