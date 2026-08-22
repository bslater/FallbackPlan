using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The drill behind ADR-0043's wiring: a full publish and restore must produce
/// records from the planes <em>below</em> the orchestrator, not only from the
/// orchestrator itself.
/// </summary>
/// <remarks>
/// Declaring a <c>[LoggerMessage]</c> and reaching it are two different acts,
/// and the gap between them is invisible to the compiler: a host that forgets
/// to hand over a logger still builds, still backs up, and still writes a log
/// file with nothing in it from the layer you wanted. The architecture suite
/// proves every declaration has a <em>call site</em>; only a run proves the
/// logger arrives there. That is what this asserts, by event id, so a lost
/// parameter somewhere in the chain from the publication down through
/// <c>FileArchiver</c>, <c>ArchiveSession</c> and <c>ManifestBuilder</c> to
/// <c>BlobWriter</c> fails here rather than in a support call.
/// </remarks>
[TestClass]
public sealed class EnginePlaneLoggingTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private const int BlobSealed = 1610;
    private const int BlobOpened = 1611;
    private const int CatalogueOpened = 1800;
    private const int PublicationFailed = 2003;
    private const int RepositoryOpened = 2031;

    private async Task<RecordingLogger> PublishAndRestoreAsync()
    {
        var log = new RecordingLogger();

        var source = new FakeFileSystemSource();
        source.AddFile("documents/alpha.bin", [.. Enumerable.Range(0, 120_000).Select(i => (byte)i)]);
        source.AddFile("documents/beta.bin", [.. Enumerable.Range(0, 90_000).Select(i => (byte)(i * 7))]);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = CatalogueDb.Open(
            Path.Combine(SpoolDirectory, "catalogue.db"), Repo, log);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue, progress: null, logger: log);

        var snapshotId = Enumerable.Repeat((byte)0xB2, 16).ToArray();
        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = source,
                Roots = [new ScanRoot("/")],
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = snapshotId,
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "engine-plane-logging-tests/1.0",
            },
            CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store, log);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, string.Empty, target);
        await new RestoreExecutor(reader, target, log).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "restored"),
            new RestoreExecutionOptions { RunId = "test", NowUnixMilliseconds = 1_722_600_000_001 },
            CancellationToken.None);

        return log;
    }

    [TestMethod]
    public async Task Publishing_WithALoggerOnTheOrchestrator_ReachesThePackingPlane()
    {
        var log = await PublishAndRestoreAsync();

        // Both planes, deliberately. Data blobs are sealed by ArchiveSession
        // and metadata blobs by ManifestBuilder — different chains of
        // constructors from the same orchestrator — so asserting only that
        // "something sealed" passes with the data plane's logger removed.
        // That is not hypothetical: it is what this test did before the
        // sealed record named its class.
        var sealedClasses = log.Records
            .Where(record => record.EventId == BlobSealed)
            .Select(record => record.Values.First(value => value.Key == "Class").Value)
            .ToArray();

        Assert.Contains(
            BlobClass.Data, sealedClasses,
            "No data blob was reported sealed, so the logger stops somewhere between the "
            + "publication and BlobWriter on the content path — the blobs were still written, "
            + "which is why nothing else notices.");

        Assert.Contains(
            BlobClass.Metadata, sealedClasses,
            "No metadata blob was reported sealed: ManifestBuilder is not handing its logger "
            + "to the writer it creates.");
    }

    [TestMethod]
    public async Task Restoring_WithALoggerOnTheReader_ReachesTheBlobReader()
    {
        var log = await PublishAndRestoreAsync();

        Assert.IsNotEmpty(
            log.Records.Where(record => record.EventId == BlobOpened),
            "No blob-opened record arrived: RepositoryReader is not handing its logger to "
            + "BlobReader.OpenAsync, so the read side of a restore is silent.");
    }

    [TestMethod]
    public async Task Publishing_WhenTheSourceThrows_NamesTheStepItDiedAfter()
    {
        // "The backup failed" is already visible from the job's outcome. Which
        // step it died in is recorded nowhere else, and it is the difference
        // between a source that could not be read, a destination that would not
        // take bytes, and an index that would not publish.
        var log = new RecordingLogger();

        var source = new FakeFileSystemSource();
        source.AddFile("documents/alpha.bin", [.. Enumerable.Range(0, 4096).Select(i => (byte)i)]);

        // The blob upload is armed to fail, not the scan: it puts the throw
        // after two steps have completed, so the record has a step to name and
        // the assertion can tell a real answer from the initial value.
        var store = new PutFaultingObjectStore(CreateStore());
        store.Arm(key => key.StartsWith("blobs/", StringComparison.Ordinal));

        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue: null, progress: null, logger: log);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await orchestrator.PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0xC3, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "engine-plane-logging-tests/1.0",
                },
                CancellationToken.None));

        var failure = log.Records.Single(record => record.EventId == PublicationFailed);
        Assert.AreEqual(
            PublicationStep.ScanSource, failure.Values.First(value => value.Key == "Step").Value,
            "the scan completed and the seal-and-upload step did not, which is exactly the "
            + "distinction this record exists to draw");
        Assert.IsInstanceOfType<IOException>(failure.Exception);
    }

    [TestMethod]
    public async Task Opening_ARepositoryWithALogger_SaysWhatItOpened()
    {
        var log = new RecordingLogger();
        var store = CreateStore();
        using var passphrase = Passphrase.Create("engine-plane-logging-passphrase!!");

        using var created = await RepositoryLifecycle.CreateAsync(
            store, passphrase, RepositoryCreationSettings.Default, 1_722_600_000_000, CancellationToken.None, log);
        using var opened = await RepositoryLifecycle.OpenAsync(store, passphrase, CancellationToken.None, log);

        Assert.ContainsSingle(log.Records.Where(record => record.EventId == RepositoryOpened));
    }

    [TestMethod]
    public async Task Opening_WithTheWrongPassphrase_RecordsTheRefusalRatherThanOnlyThrowing()
    {
        var log = new RecordingLogger();
        var store = CreateStore();
        using var passphrase = Passphrase.Create("engine-plane-logging-passphrase!!");
        using (await RepositoryLifecycle.CreateAsync(
            store, passphrase, RepositoryCreationSettings.Default, 1_722_600_000_000, CancellationToken.None))
        {
            // Created without a logger on purpose: the refusal below must be
            // recorded by the open, not carried over from the create.
        }

        using var wrong = Passphrase.Create("not the passphrase at all!!!!");
        await Assert.ThrowsExactlyAsync<KeyUnwrapFailedException>(async () =>
            await RepositoryLifecycle.OpenAsync(store, wrong, CancellationToken.None, log));

        Assert.ContainsSingle(
            log.Records.Where(record => record.EventId == 2032),
            "a refused open is the event an operator is looking for, and it used to leave no trace");
    }

    [TestMethod]
    public async Task Opening_ACatalogueWithALogger_RecordsHowTheCacheOnDiskWasTreated()
    {
        var log = await PublishAndRestoreAsync();

        Assert.ContainsSingle(
            log.Records.Where(record => record.EventId == CatalogueOpened),
            "The catalogue open is the one place that knows whether the cache survived; "
            + "exactly one open happened in this pass.");
    }
}
