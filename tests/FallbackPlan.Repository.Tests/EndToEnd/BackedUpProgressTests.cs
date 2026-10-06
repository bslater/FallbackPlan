using FallbackPlan.Domain;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What a backup says it has backed up (FR-SVC-006, ADR-0088): the plan's
/// bytes count only once the store has acknowledged them, an unchanged file
/// counts at once because its content is already stored, and the work a
/// publication does after the last byte — one source-identity hint per new
/// file version, written several at a time and all before the snapshot
/// record — is counted as it lands.
/// </summary>
/// <remarks>
/// The store under the publication holds each data blob's put until the
/// test lets it through, so "read but not yet stored" is a state the test
/// can stand in rather than a race it hopes to catch.
/// </remarks>
[TestClass]
public sealed class BackedUpProgressTests : ArchiveTestHarness
{
    private const int FileBytes = 4096;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        IJobProgressReporter? progress,
        CatalogueDb? catalogue = null) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue,
            progress: progress);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    /// <summary>Bytes no compressor shrinks, so blobs fill at the rate the test counts on.</summary>
    private static byte[] Incompressible(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [TestMethod]
    public async Task TreePublication_ContentCountsAsBackedUp_OnlyOnceTheStoreHasAcknowledgedIt()
    {
        // A hundred 4 KiB files against 256 KiB blob targets: the first blob
        // seals partway through the walk, the rest ride the blob the flush
        // seals. Both are held at the store.
        var source = new FakeFileSystemSource();
        for (var i = 0; i < 100; i++)
        {
            source.AddFile($"docs/file-{i:d3}.bin", Incompressible(FileBytes, i));
        }

        var store = new HeldUploadStore(CreateStore());
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var reporter = new RecordingReporter();
        using var timeout = new CancellationTokenSource(Patience);

        var run = CreateOrchestrator(store, keys, credential, reporter)
            .PublishAsync(Job(source, 0x11), CancellationToken.None).AsTask();

        // Every file has been read and the run is flushing — and nothing is
        // backed up, because no blob has been acknowledged.
        var read = await reporter.WaitForAsync(report => report.State == JobState.Uploading, timeout.Token);
        Assert.AreEqual(100L, read.FilesDone);
        Assert.AreEqual(100L * FileBytes, read.TotalBytes);
        Assert.AreEqual(0L, read.BytesBackedUp, "content read but not acknowledged by the store is not backed up");

        // The first blob lands; the second is still held. What it carried
        // counts, and nothing past it: whole files here, since each 4 KiB
        // file is one segment and segments never straddle a blob.
        await store.WaitForHeldAsync(2, timeout.Token);
        store.ReleaseFirst();
        var partly = await reporter.WaitForAsync(report => report.BytesBackedUp > 0, timeout.Token);
        Assert.IsLessThan(100L * FileBytes, partly.BytesBackedUp!.Value, "the held blob's files must not count");
        Assert.AreEqual(0L, partly.BytesBackedUp.Value % FileBytes, "a one-segment file is counted whole or not at all");
        Assert.AreEqual(JobState.Uploading, partly.State);

        store.ReleaseAll();
        await run;
        Assert.AreEqual(100L * FileBytes, reporter.Reports[^1].BytesBackedUp, "a published run has backed up its whole plan");
    }

    [TestMethod]
    public async Task IncrementalPublication_AnUnchangedFile_CountsAsBackedUpBeforeAnyUpload()
    {
        // Its content is already at the store, so an unchanged file is
        // backed up the moment the run decides to reuse it — while the one
        // changed file's blob is still held.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/big.bin", Incompressible(300_000, 3));
        source.AddFile("docs/small.txt", Incompressible(500, 5));
        source.AddFile("readme.md", Incompressible(2_000, 7));

        var plain = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        using var timeout = new CancellationTokenSource(Patience);

        await CreateOrchestrator(plain, keys, credential, progress: null, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var changed = source.AddFile("readme.md", Incompressible(2_500, 9));
        changed.Metadata = changed.Metadata with { ModifiedAt = 1_722_700_000_000 };

        var store = new HeldUploadStore(plain);
        var reporter = new RecordingReporter();
        var run = CreateOrchestrator(store, keys, credential, reporter, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None).AsTask();

        var flushing = await reporter.WaitForAsync(report => report.State == JobState.Uploading, timeout.Token);
        Assert.AreEqual(2L, flushing.FilesReused);
        Assert.AreEqual(303_000L, flushing.TotalBytes);
        Assert.AreEqual(300_500L, flushing.BytesBackedUp, "the two unchanged files, and not the changed one");

        store.ReleaseAll();
        await run;
        Assert.AreEqual(303_000L, reporter.Reports[^1].BytesBackedUp);
    }

    [TestMethod]
    public async Task TreePublication_TheFinishingWork_IsCountedAsItLands()
    {
        // The source-identity hints are written after the last data byte
        // and before the snapshot record: the run's finishing work. Their
        // count appears once there is one, and the run's last report says
        // they all landed.
        var source = new FakeFileSystemSource();
        for (var i = 0; i < 20; i++)
        {
            source.AddFile($"docs/file-{i:d2}.bin", Incompressible(1_000, i));
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var reporter = new RecordingReporter();

        await CreateOrchestrator(store, keys, credential, reporter)
            .PublishAsync(Job(source, 0x21), CancellationToken.None);

        var reports = reporter.Reports;
        Assert.IsTrue(
            reports.TakeWhile(report => report.State != JobState.Publishing).All(report => report.HintsTotal is null),
            "nothing is owed to the finishing count before the run is finishing");
        Assert.Contains(report => report.State == JobState.Publishing && report.HintsTotal == 20L, reports);
        Assert.IsTrue(
            reports.All(report => report.HintsWritten is null || report.HintsWritten <= report.HintsTotal),
            "never more written than owed");

        var last = reports[^1];
        Assert.AreEqual(20L, last.HintsTotal);
        Assert.AreEqual(20L, last.HintsWritten);
    }

    [TestMethod]
    public async Task TreePublication_IdentityHints_AreWrittenSeveralAtATime_AndAllBeforeTheSnapshotRecord()
    {
        // Each hint is one small object, so writing them one after another
        // spends a store round trip per file — minutes on a first backup of
        // a few thousand files. Several at once, bounded; and the snapshot
        // record still waits for the last of them (06 §11).
        var source = new FakeFileSystemSource();
        for (var i = 0; i < 48; i++)
        {
            source.AddFile($"docs/file-{i:d2}.bin", Incompressible(500, i));
        }

        var store = new PacedHintStore(CreateStore(), TimeSpan.FromMilliseconds(20));
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        await CreateOrchestrator(store, keys, credential, progress: null)
            .PublishAsync(Job(source, 0x31), CancellationToken.None);

        Assert.AreEqual(48, store.HintsWritten);
        Assert.IsGreaterThan(1, store.MostInFlight, "the hints were written one at a time");
        Assert.IsLessThanOrEqualTo(ManifestBuilder.HintWritesInFlight, store.MostInFlight);
        Assert.AreEqual(48, store.HintsWrittenWhenTheSnapshotWas, "the snapshot record went out before every hint had landed");
    }

    /// <summary>Keeps every report, in order, and lets a test wait for one.</summary>
    private sealed class RecordingReporter : IJobProgressReporter
    {
        private readonly List<JobProgress> _reports = [];
        private readonly List<(Func<JobProgress, bool> Predicate, TaskCompletionSource<JobProgress> Seen)> _waiters = [];

        public IReadOnlyList<JobProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(JobProgress progress)
        {
            lock (_reports)
            {
                _reports.Add(progress);
                foreach (var waiter in _waiters.Where(waiter => waiter.Predicate(progress)).ToList())
                {
                    _waiters.Remove(waiter);
                    waiter.Seen.TrySetResult(progress);
                }
            }
        }

        /// <summary>The first report, past or future, that satisfies <paramref name="predicate"/>.</summary>
        public Task<JobProgress> WaitForAsync(Func<JobProgress, bool> predicate, CancellationToken cancellationToken)
        {
            lock (_reports)
            {
                var seen = _reports.FirstOrDefault(predicate);
                if (seen is not null)
                {
                    return Task.FromResult(seen);
                }

                var waiter = new TaskCompletionSource<JobProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((predicate, waiter));
                return waiter.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Holds every data blob's put until the test lets it through, in the
    /// order the puts arrived. Every other object passes straight through.
    /// </summary>
    private sealed class HeldUploadStore(IObjectStore inner) : IObjectStore
    {
        private readonly List<TaskCompletionSource> _held = [];
        private int _released;
        private bool _open;

        public StoreCapabilities Capabilities => inner.Capabilities;

        public async Task WaitForHeldAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_held)
                {
                    if (_held.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }
        }

        public void ReleaseFirst()
        {
            lock (_held)
            {
                _held[_released++].TrySetResult();
            }
        }

        public void ReleaseAll()
        {
            lock (_held)
            {
                _open = true;
                foreach (var hold in _held)
                {
                    hold.TrySetResult();
                }
            }
        }

        public async ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            if (key.Value.StartsWith("blobs/data/", StringComparison.Ordinal))
            {
                TaskCompletionSource? hold = null;
                lock (_held)
                {
                    if (!_open)
                    {
                        hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _held.Add(hold);
                    }
                }

                if (hold is not null)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
            }

            return await inner.PutAsync(key, openContent, conditions, cancellationToken);
        }

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(
            ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }

    /// <summary>
    /// Gives every source-identity hint a store round trip of a fixed length,
    /// and notes how many were in flight at once and how many had landed when
    /// the snapshot record was put.
    /// </summary>
    private sealed class PacedHintStore(IObjectStore inner, TimeSpan roundTrip) : IObjectStore
    {
        private int _inFlight;
        private int _mostInFlight;
        private int _written;
        private int _writtenAtSnapshot = -1;

        public int MostInFlight => Volatile.Read(ref _mostInFlight);

        public int HintsWritten => Volatile.Read(ref _written);

        public int HintsWrittenWhenTheSnapshotWas => Volatile.Read(ref _writtenAtSnapshot);

        public StoreCapabilities Capabilities => inner.Capabilities;

        public async ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            if (key.Value.StartsWith("snapshots/", StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref _writtenAtSnapshot, Volatile.Read(ref _written), -1);
            }

            if (!key.Value.StartsWith("hints/identity/", StringComparison.Ordinal))
            {
                return await inner.PutAsync(key, openContent, conditions, cancellationToken);
            }

            var now = Interlocked.Increment(ref _inFlight);
            int most;
            while (now > (most = Volatile.Read(ref _mostInFlight)) &&
                   Interlocked.CompareExchange(ref _mostInFlight, now, most) != most)
            {
            }

            try
            {
                await Task.Delay(roundTrip, cancellationToken);
                return await inner.PutAsync(key, openContent, conditions, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref _written);
            }
        }

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(
            ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
