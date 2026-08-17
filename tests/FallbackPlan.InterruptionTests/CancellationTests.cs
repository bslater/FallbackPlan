using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// Backup-side cancellation (ADR-0029 §4; NFR-REL-001; the prior-art
/// review's T-2): a cancel is a command whose durable outcome is the same
/// <em>state class</em> as a kill at the same point — blobs intent-covered,
/// nothing referenced, nothing collectable, every earlier snapshot untouched
/// — and whose leftovers the next publication absorbs without repair or
/// operator action.
/// </summary>
/// <remarks>
/// One behaviour is pinned deliberately rather than fixed: a cancel drains
/// the buffered and in-flight blob uploads through session disposal, writing
/// to the store <em>after</em> the request. Those writes are intent-covered
/// like every other upload and bounded by the queue, and T-2's acceptance is
/// state-class equivalence plus a clean rerun — not an instant stop, which
/// would trade a bounded drain for a torn upload pipeline.
/// </remarks>
[TestClass]
public sealed class CancellationTests : InterruptionHarness
{
    private const int Outstanding = 4;

    /// <summary>A sibling world under the harness root, cleaned up with it.</summary>
    private (LocalFileSystemObjectStore Store, string StoreRoot, string SpoolDirectory) CreateWorld(string name)
    {
        var root = Path.Combine(Path.GetDirectoryName(SpoolDirectory)!, name);
        return (new LocalFileSystemObjectStore(Path.Combine(root, "store")), Path.Combine(root, "store"), Path.Combine(root, "spool"));
    }

    private static int Count(string storeRoot, string prefix)
    {
        var path = Path.Combine(storeRoot, prefix.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    /// <summary>
    /// Runs a publication over <paramref name="store"/> gated at
    /// <see cref="Outstanding"/> parked blob uploads, cancels while they are
    /// parked, releases, and awaits the cancellation surfacing.
    /// </summary>
    private async Task CancelMidUploadAsync(
        Storage.Abstractions.IObjectStore store,
        RepositoryKeySet keys,
        Repository.Crypto.KeyHierarchy hierarchy,
        byte[] content,
        string? spoolDirectory = null)
    {
        var gate = new GatingObjectStore(store, Outstanding, key => key.StartsWith("blobs/", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();

        using var source = new MemoryStream(content);
        var publication = CreateOrchestrator(gate, keys, hierarchy, concurrency: Outstanding, spoolDirectory: spoolDirectory)
            .PublishAsync(Job(source, snapshotSeed: 0xB2), cancellation.Token).AsTask();

        Assert.IsTrue(
            await gate.WaitUntilParkedAsync(TimeSpan.FromSeconds(30)),
            "the gate never held the uploads this test cancels across.");

        await cancellation.CancelAsync();
        gate.Release();

        try
        {
            await publication;
            Assert.Fail("the publication completed despite the cancel.");
        }
        catch (OperationCanceledException)
        {
            // The command landed. The drain that ran between the cancel and
            // this throw is the pinned behaviour the class remarks describe.
        }
    }

    /// <summary>
    /// The row-4 state class of architecture 04 §5.1, asserted structurally:
    /// what a kill and a cancel must both leave, whatever their raw counts.
    /// </summary>
    private static async Task AssertUploadInterruptionStateClassAsync(
        LocalFileSystemObjectStore store,
        string storeRoot,
        Repository.Crypto.KeyHierarchy hierarchy)
    {
        Assert.IsTrue(Count(storeRoot, "blobs") > 0);
        Assert.AreEqual(0, Count(storeRoot, "index/delta"));
        Assert.AreEqual(0, Count(storeRoot, "snapshots"));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, currentGeneration: 0, nowMs: 1_722_600_000_000, skewMarginMs: 0);
        Assert.IsNotEmpty(survey.LiveIntents);

        foreach (var (key, blobId) in await ReadStoredBlobsAsync(store))
        {
            Assert.IsTrue(
                survey.IsCovered(blobId),
                $"blob '{key}' is durable but no live intent covers it — a collector would delete it (08 §8).");
        }

        // The strongest oracle: a collector running right now would classify
        // every durable blob as protected, with no heuristics.
        Assert.IsEmpty(await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000));
    }

    [TestMethod]
    public async Task Publish_CancelledMidUpload_LeavesTheSameStateAsAKillAtThatPoint()
    {
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();
        var content = BuildFile(seed: 2, regions: 40);

        // Twin worlds driven with the same content. World one: a kill at the
        // step-4 boundary. World two: a cancel fired while uploads were
        // parked in flight. Raw counts legitimately differ — the kill ran
        // every upload, the cancel drained what was queued — so the claim is
        // the state CLASS, not byte equality.
        var (killStore, killRoot, killSpool) = CreateWorld("kill");
        using (var source = new MemoryStream(content))
        {
            await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(
                        killStore, keys, hierarchy, new KillAfter(PublicationStep.UploadBlobs),
                        concurrency: Outstanding, spoolDirectory: killSpool)
                    .PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
        }

        var (cancelStore, cancelRoot, cancelSpool) = CreateWorld("cancel");
        await CancelMidUploadAsync(cancelStore, keys, hierarchy, content, cancelSpool);

        await AssertUploadInterruptionStateClassAsync(killStore, killRoot, hierarchy);
        await AssertUploadInterruptionStateClassAsync(cancelStore, cancelRoot, hierarchy);

        // The pinned drain: every upload parked at the cancel still became
        // durable — writes after the request, intent-covered, queue-bounded.
        Assert.IsTrue(Count(cancelRoot, "blobs") >= Outstanding);
    }

    [TestMethod]
    public async Task Publish_CancelledMidUpload_LeavesEveryEarlierSnapshotRestorable()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        await CancelMidUploadAsync(store, keys, hierarchy, BuildFile(seed: 2, regions: 40));

        // The load-bearing claim, identical to the kill matrix's: no cancel
        // at any point makes a committed snapshot unreadable (04 §5.1).
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
    }

    [TestMethod]
    public async Task Publish_CancelledThenRerun_CompletesWithoutRepairOrOperatorAction()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var blobsBeforeCancel = (await ReadStoredBlobsAsync(store)).Select(blob => blob.Key.ToString()).ToHashSet();
        await CancelMidUploadAsync(store, keys, hierarchy, BuildFile(seed: 2, regions: 40));
        var cancelledRunBlobs = (await ReadStoredBlobsAsync(store))
            .Select(blob => blob.Key.ToString())
            .Where(key => !blobsBeforeCancel.Contains(key))
            .ToHashSet();
        Assert.IsNotEmpty(cancelledRunBlobs);

        // A fresh process life over the durable world the cancel left: no
        // repair step, no operator action, a new snapshot identity — retry
        // of a cancelled job is a new publication, not a resumed one.
        var retried = BuildFile(seed: 3);
        using (var source = new MemoryStream(retried))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(retried, await RestoreSnapshotAsync(store, keys, 0xC3));

        // Every number the cancelled run allocated is now accounted for —
        // by the object that used it or by the void delta the rerun owed it
        // (ADR-0029 §4) — so nothing is left for any later run to repair.
        var lines = await File.ReadAllLinesAsync(Path.Combine(SpoolDirectory, "sequence.txt"), CancellationToken.None);
        Assert.AreEqual("pending=", lines.Single(line => line.StartsWith("pending=", StringComparison.Ordinal)));

        // And the cancelled run's blobs stay collector-safe throughout: its
        // intent never retired, so coverage stands until expiry (08 §8). The
        // completed runs' blobs legitimately show as candidates to this
        // intents-only simulation — they are reachable through their
        // snapshots instead, exactly the release-timing pinned by
        // ConcurrentCollectionTests — so the claim is scoped to the
        // cancelled run's.
        var candidates = await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000);
        foreach (var candidate in candidates)
        {
            Assert.IsFalse(
                cancelledRunBlobs.Contains(candidate.ToString()),
                $"blob '{candidate}' belongs to the cancelled run and must stay intent-covered until expiry (08 §8).");
        }
    }

    [TestMethod]
    public async Task Publish_CancelledDuringTheSnapshotWrite_PublishesNoSnapshotOrACompleteOne()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var content = BuildFile(seed: 2);
        var holding = new HoldingObjectStore(store, parkTarget: 1, key => key.StartsWith("snapshots/", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();

        Task publication;
        using (var source = new MemoryStream(content))
        {
            publication = CreateOrchestrator(holding, keys, hierarchy)
                .PublishAsync(Job(source, snapshotSeed: 0xB2), cancellation.Token).AsTask();

            Assert.IsTrue(
                await holding.WaitUntilParkedAsync(TimeSpan.FromSeconds(30)),
                "the snapshot put never arrived at the hold this test cancels across.");

            await cancellation.CancelAsync();
            holding.Release();

            try
            {
                await publication;
                Assert.Fail("the publication completed despite the cancel.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The snapshot is one put through temp-plus-rename, so a cancel that
        // lands inside step 7 has exactly two durable outcomes: no snapshot
        // object at all, or a complete restorable one. A partial object is
        // structurally impossible, and this pins that there is no third path.
        if (CountUnder("snapshots") == 0)
        {
            return;
        }

        SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, 0xB2));
    }
}
