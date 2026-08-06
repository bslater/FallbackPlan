using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The architecture 04 §5.1 interruption matrix, row by row (F1;
/// NFR-REL-001, exit criterion 5): a kill after each publication step leaves
/// exactly the row's state, a previously committed snapshot stays
/// restorable through every one of them, and a fresh process completes the
/// job afterwards. The step-3 row (partial spool, nothing uploaded) is the
/// C1 spool suite's territory — SpoolCheckpointTests proves byte-identical
/// resume at the unit level and BlobSpoolResumeTests proves it through the
/// orchestrator — and its store-side claim is asserted here.
/// </summary>
public sealed class PublicationInterruptionTests : InterruptionHarness
{
    private async Task<byte[]> PublishBaselineAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        Repository.Crypto.KeyHierarchy hierarchy)
    {
        var baseline = BuildFile(seed: 1);
        using var source = new MemoryStream(baseline);
        await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        return baseline;
    }

    private async Task<PublicationKilledException> KillSecondPublicationAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        Repository.Crypto.KeyHierarchy hierarchy,
        byte[] data,
        PublicationStep killAfter)
    {
        using var source = new MemoryStream(data);
        return await Assert.ThrowsAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(killAfter))
                .PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
    }

    // An explicit initializer rather than a collection expression: Visual
    // Studio's analyzer lowers the expression through a path that trips
    // CA1825 (observed on Windows), and warnings are errors everywhere.
    public static TheoryData<PublicationStep> KillPoints() => new()
    {
        PublicationStep.PublishIntent,
        PublicationStep.UploadBlobs,
        PublicationStep.PublishIndexDeltas,
        PublicationStep.PublishSnapshot,
        PublicationStep.RetireIntent,
    };

    [Theory]
    [MemberData(nameof(KillPoints))]
    public async Task No_kill_point_makes_the_committed_snapshot_unreadable_and_a_fresh_process_completes(
        PublicationStep killAfter)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = await PublishBaselineAsync(store, keys, hierarchy);
        var second = BuildFile(seed: 2);

        await KillSecondPublicationAsync(store, keys, hierarchy, second, killAfter);

        // The load-bearing claim of the whole matrix (04 §5.1): no
        // interruption at any step makes the committed snapshot unreadable.
        Assert.Equal(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));

        // A fresh process completes the job — the durable world it inherits
        // is exactly what the kill left. New snapshot id: retry of an
        // interrupted job is a new publication, not a resumed identity.
        using (var retry = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(retry, snapshotSeed: 0xC3), CancellationToken.None);
        }

        Assert.Equal(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
        Assert.Equal(second, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    [Fact]
    public async Task Row_1_intent_published_no_blobs()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var blobsBefore = CountUnder("blobs");
        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.PublishIntent);

        // "Intent published, no blobs": nothing collectable was written.
        Assert.Equal(blobsBefore, CountUnder("blobs"));
        Assert.Equal(1, CountUnder("journal"));
    }

    [Fact]
    public async Task Row_4_blobs_durable_unreferenced_and_intent_covered()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.UploadBlobs);

        // "Blobs durable, unreferenced": blobs exist, no delta, no snapshot.
        Assert.True(CountUnder("blobs") > 0);
        Assert.Equal(0, CountUnder("index/delta"));
        Assert.Equal(0, CountUnder("snapshots"));

        // "Intent keeps them reachable": every uploaded blob is covered by a
        // live intent — the survey a collector must run (08 §8).
        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, currentGeneration: 0, nowMs: 1_722_600_000_000, skewMarginMs: 0);

        Assert.NotEmpty(survey.LiveIntents);
        Assert.True(survey.LiveIntents.SelectMany(intent => intent.CoveredBlobIds).Any(),
            "the uploaded blobs must be named by the live intent's extensions");
    }

    [Fact]
    public async Task Row_6_deltas_published_no_snapshot()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.PublishIndexDeltas);

        // "Deltas published, no snapshot": harmless index entries; blobs
        // stay intent-covered until retirement or expiry.
        Assert.True(CountUnder("index/delta") > 0);
        Assert.Equal(0, CountUnder("snapshots"));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.NotEmpty(survey.LiveIntents);
    }

    [Fact]
    public async Task Row_7_snapshot_published_intent_live_snapshot_restorable()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var second = BuildFile(seed: 2);
        await KillSecondPublicationAsync(store, keys, hierarchy, second, PublicationStep.PublishSnapshot);

        // "Snapshot is valid and restorable; intent retires on next run or
        // expires" — the snapshot works even though the intent never retired.
        Assert.Equal(second, await RestoreSnapshotAsync(store, keys, 0xB2));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.NotEmpty(survey.LiveIntents);
    }

    [Fact]
    public async Task Row_8_complete_the_intent_is_retired()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var second = BuildFile(seed: 2);
        await KillSecondPublicationAsync(store, keys, hierarchy, second, PublicationStep.RetireIntent);

        Assert.Equal(second, await RestoreSnapshotAsync(store, keys, 0xB2));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.Empty(survey.LiveIntents);
    }

    [Fact]
    public async Task Row_3_a_partial_spool_uploads_nothing()
    {
        // The step-3 row's store-side claim: a job that dies before any seal
        // leaves a spool (C1's resumable state) and NOTHING in the store.
        // Byte-identical resume of that spool is proven by SpoolCheckpointTests
        // and, through the orchestrator, by BlobSpoolResumeTests.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var faulting = new FaultInjectingObjectStore(store, putBudget: 1); // the intent put succeeds; the first blob put dies

        using var source = new MemoryStream(BuildFile(seed: 3));
        await Assert.ThrowsAsync<IOException>(async () =>
            await CreateOrchestrator(faulting, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xD4), CancellationToken.None));

        Assert.Equal(0, CountUnder("blobs"));
    }
}
