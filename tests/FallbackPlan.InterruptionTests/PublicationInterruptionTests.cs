using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

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
[TestClass]
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
        return await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(killAfter))
                .PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
    }

    // Every one of the nine step boundaries (04 §5.1), not only those with a
    // distinct durable state: the boundary after step 2 equals step 1's
    // state and the one after step 5 equals step 4's — by construction on
    // this path — and the boundary after step 9 fails the caller over an
    // already-committed publication. The universal claims (the committed
    // snapshot stays readable, a fresh process completes the job) must hold
    // at all nine, and running the equivalent rows is what keeps that true
    // if a step ever grows durable effects of its own.
    //
    // An explicit initializer rather than a collection expression: Visual
    // Studio's analyzer lowers the expression through a path that trips
    // CA1825 (observed on Windows), and warnings are errors everywhere.
    public static IEnumerable<object[]> KillPoints() =>
    [
        [PublicationStep.PublishIntent],
        [PublicationStep.ScanSource],
        [PublicationStep.SegmentAndSeal],
        [PublicationStep.UploadBlobs],
        [PublicationStep.VerifyAcknowledgements],
        [PublicationStep.PublishIndexDeltas],
        [PublicationStep.PublishSnapshot],
        [PublicationStep.RetireIntent],
        [PublicationStep.Complete],
    ];

    [TestMethod]
    [DynamicData(nameof(KillPoints))]
    public async Task Publish_KilledAtAnyStep_LeavesTheCommittedSnapshotReadableAndCompletesOnRerun(
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
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));

        // A fresh process completes the job — the durable world it inherits
        // is exactly what the kill left. New snapshot id: retry of an
        // interrupted job is a new publication, not a resumed identity.
        using (var retry = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(retry, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    [TestMethod]
    public async Task Publication_KilledAfterTheIntent_LeavesNothingCollectable()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var blobsBefore = CountUnder("blobs");
        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.PublishIntent);

        // "Intent published, no blobs": nothing collectable was written.
        Assert.AreEqual(blobsBefore, CountUnder("blobs"));
        Assert.AreEqual(1, CountUnder("journal"));
    }

    [TestMethod]
    public async Task Publication_KilledWithBlobsDurableAndUnreferenced_LeavesThemIntentCovered()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.UploadBlobs);

        // "Blobs durable, unreferenced": blobs exist, no delta, no snapshot.
        Assert.IsTrue(CountUnder("blobs") > 0);
        Assert.AreEqual(0, CountUnder("index/delta"));
        Assert.AreEqual(0, CountUnder("snapshots"));

        // "Intent keeps them reachable": every uploaded blob is covered by a
        // live intent — the survey a collector must run (08 §8).
        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, currentGeneration: 0, nowMs: 1_722_600_000_000, skewMarginMs: 0);

        Assert.IsNotEmpty(survey.LiveIntents);

        // Every blob, not merely some blob: the row's claim is that a collector
        // running now would delete none of them, and one covered blob out of
        // several would still lose data.
        var stored = await ReadStoredBlobsAsync(store);
        Assert.IsNotEmpty(stored);

        foreach (var (key, blobId) in stored)
        {
            Assert.IsTrue(
                survey.IsCovered(blobId),
                $"blob '{key}' is durable but no live intent covers it — a collector would delete it (08 §8, C4).");
        }
    }

    [TestMethod]
    public async Task Publication_KilledAfterTheDeltas_LeavesNoSnapshotAndNoOrphanedIndex()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        await KillSecondPublicationAsync(store, keys, hierarchy, BuildFile(seed: 2), PublicationStep.PublishIndexDeltas);

        // "Deltas published, no snapshot": harmless index entries; blobs
        // stay intent-covered until retirement or expiry.
        Assert.IsTrue(CountUnder("index/delta") > 0);
        Assert.AreEqual(0, CountUnder("snapshots"));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.IsNotEmpty(survey.LiveIntents);
    }

    [TestMethod]
    public async Task Publication_KilledAfterTheSnapshotWithTheIntentLive_LeavesTheSnapshotRestorable()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var second = BuildFile(seed: 2);
        await KillSecondPublicationAsync(store, keys, hierarchy, second, PublicationStep.PublishSnapshot);

        // "Snapshot is valid and restorable; intent retires on next run or
        // expires" — the snapshot works even though the intent never retired.
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.IsNotEmpty(survey.LiveIntents);
    }

    [TestMethod]
    public async Task Publication_RunsToCompletion_RetiresItsIntent()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var second = BuildFile(seed: 2);
        await KillSecondPublicationAsync(store, keys, hierarchy, second, PublicationStep.RetireIntent);

        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(0, CancellationToken.None);
        var survey = IntentSurveyor.Survey(records, unparseable, 0, 1_722_600_000_000, 0);
        Assert.IsEmpty(survey.LiveIntents);
    }

    [TestMethod]
    public async Task Publication_KilledMidSpool_UploadsNothing()
    {
        // The step-3 row's store-side claim: a job that dies before any seal
        // leaves a spool (C1's resumable state) and NOTHING in the store.
        // Byte-identical resume of that spool is proven by SpoolCheckpointTests
        // and, through the orchestrator, by BlobSpoolResumeTests.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // The intent put succeeds; the next put — the first blob's covering
        // extension, which precedes its blob put (08 §3.1) — dies, so no
        // blob byte ever reaches the store.
        var faulting = new FaultInjectingObjectStore(store, putBudget: 1);

        using var source = new MemoryStream(BuildFile(seed: 3));
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await CreateOrchestrator(faulting, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xD4), CancellationToken.None));

        Assert.AreEqual(0, CountUnder("blobs"));
    }

    [TestMethod]
    public async Task Publication_AnUploadFails_FailsTheJobRatherThanParkingTheProducer()
    {
        // Row 3 kills the upload too early to reach this: the failure has to
        // outlast the queue. Here the store dies on the first blob put and the
        // file seals far more blobs than the channel holds, so the producer
        // arrives at a full queue with every worker already dead. A worker that
        // stops reading without completing the channel strands it there — the
        // job then neither finishes nor fails, which is worse than either.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var faulting = new FaultInjectingObjectStore(store, putBudget: 1);

        using var source = new MemoryStream(BuildFile(seed: 5, regions: 40));
        var publication = CreateOrchestrator(faulting, keys, hierarchy, concurrency: 2)
            .PublishAsync(Job(source, snapshotSeed: 0xD5), CancellationToken.None).AsTask();

        var finished = await Task.WhenAny(publication, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.IsTrue(
            ReferenceEquals(finished, publication),
            "the publication never finished — the producer is parked on a queue no live worker will ever drain.");

        // And it fails with the upload's own error, not with the queue
        // plumbing that noticed the upload was gone.
        await Assert.ThrowsExactlyAsync<IOException>(async () => await publication);
    }

    [TestMethod]
    public async Task Publication_KilledWithSeveralUploadsOutstanding_LeavesThemIntentCovered()
    {
        const int Outstanding = 4;

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // Every other row-4 test reaches the state one blob at a time, and not
        // by choice: the step-4 observer fires after FlushAsync, which is the
        // drain barrier, so nothing is in flight by the time a KillAfter runs.
        // Holding the puts open is the only way to stand several blobs in the
        // row's state at once (G3).
        var gate = new GatingObjectStore(store, Outstanding);
        var orchestrator = CreateOrchestrator(
            gate, keys, hierarchy, new KillAfter(PublicationStep.UploadBlobs), concurrency: Outstanding);

        var content = BuildFile(seed: 2, regions: 40);
        using var source = new MemoryStream(content);
        var publication = orchestrator.PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None).AsTask();

        Assert.IsTrue(
            await gate.WaitUntilParkedAsync(TimeSpan.FromSeconds(30)),
            $"only {gate.ParkedCount} of {Outstanding} blob uploads were ever outstanding at once — the gate never held "
            + "the state this test exists to inspect.");

        // The moment of interest: four blobs durable, none referenced, the
        // uploads that put them there still in flight. Coverage has to hold for
        // every one of them, because coverage is the only thing standing between
        // a blob its job has not finished with and a collector (08 §3.1, C4).
        using (var journalReader = new JournalReader(store, Repo, hierarchy))
        {
            var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
            var survey = IntentSurveyor.Survey(records, unparseable, currentGeneration: 0, nowMs: 1_722_600_000_000, skewMarginMs: 0);

            var inFlight = await ReadStoredBlobsAsync(store);

            // No put has been allowed to return, so the durable blobs are
            // exactly the parked ones — the count is a fact about the gate, not
            // a race to be tolerated.
            Assert.AreEqual(Outstanding, inFlight.Count);

            foreach (var (key, blobId) in inFlight)
            {
                Assert.IsTrue(
                    survey.IsCovered(blobId),
                    $"blob '{key}' was durable with its upload still outstanding and no live intent covered it (C4).");
            }

            Assert.AreEqual(0, CountUnder("index/delta"));
            Assert.AreEqual(0, CountUnder("snapshots"));
        }

        gate.Release();
        await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () => await publication);

        // Row 4 still reads the same once the kill lands, and a fresh process
        // completes the job over what it left.
        Assert.IsTrue(CountUnder("blobs") > Outstanding);
        Assert.AreEqual(0, CountUnder("index/delta"));
        Assert.AreEqual(0, CountUnder("snapshots"));

        using (var retry = new MemoryStream(content))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(retry, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    /// <summary>
    /// A store decorator that holds blob puts open once they are durable, so a
    /// test can stand several blobs in row 4's state simultaneously.
    /// </summary>
    /// <remarks>
    /// The park is deliberately <i>after</i> the inner put. Row 4 is about blobs
    /// that are durable and unreferenced, so they must be written before the
    /// state exists at all. Ordering — the covering intent before the blob's own
    /// put — is a different claim, and ConcurrentUploadTests is where it is
    /// proven; nothing here should be read as evidence for it.
    /// </remarks>
    private sealed class GatingObjectStore(IObjectStore inner, int parkTarget) : IObjectStore
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _parked;

        public int ParkedCount
        {
            get
            {
                lock (_gate)
                {
                    return _parked;
                }
            }
        }

        public StoreCapabilities Capabilities => inner.Capabilities;

        /// <summary>Completes once <c>parkTarget</c> blob puts are parked at the same time.</summary>
        public async Task<bool> WaitUntilParkedAsync(TimeSpan timeout) =>
            await Task.WhenAny(_reached.Task, Task.Delay(timeout)).ConfigureAwait(false) == _reached.Task;

        public void Release() => _release.TrySetResult();

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public async ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            var result = await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);

            if (key.ToString().StartsWith("blobs/", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    if (++_parked >= parkTarget)
                    {
                        _reached.TrySetResult();
                    }
                }

                await _release.Task.ConfigureAwait(false);
            }

            return result;
        }

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
