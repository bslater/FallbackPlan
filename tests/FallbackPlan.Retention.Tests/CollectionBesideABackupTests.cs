using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A collection pass beside a backup of the same set (FR-GC-003, FR-GC-006,
/// specification 11 §3.2). The service runs one beside the other: a backup
/// holds its set's run, and a retention pass queues under an identity of its
/// own. A backup builds on what the archive already stores, and a pass decides
/// from the snapshots already published, so neither sees the other's
/// unfinished work. Two rules keep them apart. A backup never builds on a blob
/// a pass has tombstoned: it stores those bytes again. And a pass beside an
/// in-flight backup tombstones and deletes no blob, because the backup may
/// have built on one before any tombstone was there.
/// </summary>
/// <remarks>
/// The backups that matter run through the service's own publication path,
/// held at a chosen point by an observer or a store that runs a pass there:
/// the window is the length of a backup, and a test cannot wait for chance to
/// land a pass inside it.
/// </remarks>
[TestClass]
public sealed class CollectionBesideABackupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-beside-backup-tests", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "beside-a-backup-passphrase!!";

    /// <summary>Random, so a blob holding these bytes is at least this long whatever its compression.</summary>
    private static readonly byte[] Returning = RandomBytes(seed: 1, 200 * 1024);

    private static readonly byte[] Interim = RandomBytes(seed: 2, 200 * 1024);

    private const int NewFileCount = 16;

    private const int NewFileLength = 256 * 1024;

    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");

    private string RepoPath => Path.Combine(ArchivesRoot, SetId);

    private string StateDirectory => Path.Combine(_root, "state");

    private string SourceRoot => Path.Combine(_root, "source");

    /// <summary>First in the scan, so its bytes are matched before any new blob is sealed.</summary>
    private string ReturningPath => Path.Combine(SourceRoot, "a-returning.bin");

    public CollectionBesideABackupTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = "every 4h",
                    Retention = Policy,
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(StateDirectory, "config.json"));
    }

    private static RetentionConfiguration Policy => new() { KeepDaily = 1, MinGenerations = 1 };

    [TestMethod]
    public async Task Backup_OfBytesOnlyATombstonedBlobHolds_StoresThemAgainRatherThanBuildingOnIt()
    {
        await TombstoneTheReturningBytesAsync();

        // The bytes come back while the blob holding them is condemned. A
        // backup that built on it would hang its snapshot on a blob the next
        // quiet pass deletes.
        await File.WriteAllBytesAsync(ReturningPath, Returning);
        var published = await BackUpThroughAsync(Day1.AddDays(2));

        Assert.IsGreaterThanOrEqualTo(
            Returning.Length,
            published.ContentBlobs.Sum(blob => blob.Length),
            "the backup built on a blob a pass had tombstoned");
    }

    [TestMethod]
    public async Task RetentionPass_BetweenABackupsUploadsAndItsIndex_DeletesNoBlob_AndTheBackupRestores()
    {
        await TombstoneTheReturningBytesAsync();

        // Between the backup's uploads and its index, a pass runs for which
        // the tombstoned blob's grace has run out.
        await File.WriteAllBytesAsync(ReturningPath, Returning);
        var beside = new PassAtStep(
            PublicationStep.UploadBlobs, () => RunAsync(Day1.AddDays(2)).GetAwaiter().GetResult());
        var published = await BackUpThroughAsync(Day1.AddDays(2), beside);

        Assert.IsNotNull(beside.Report, "the premise: a pass ran while the backup was in flight");
        Assert.Contains(
            line => line.StartsWith("held beside a backup:", StringComparison.Ordinal),
            beside.Report.Lines,
            "a pass deleted a blob while a backup was in flight: " + string.Join(" | ", beside.Report.Lines));
        await AssertRestoresAsync(published.SnapshotId);
    }

    [TestMethod]
    public async Task RetentionPass_BesideABackupThatBuiltOnABlobBeforeItWasCondemned_LeavesTheBackupRestorable()
    {
        // Day one stores the bytes and day two replaces them. No pass has run,
        // so nothing is tombstoned, and the next backup builds on day one's
        // blob in good faith.
        await File.WriteAllBytesAsync(ReturningPath, Returning);
        await BackUpAsync(Day1);
        await File.WriteAllBytesAsync(ReturningPath, Interim);
        await BackUpAsync(Day1.AddDays(1));

        // The bytes come back beside enough new ones that the backup names
        // more blobs than its intent did, which moves the grace clock while it
        // is still in flight. Once it has built on day one's blob, a pass
        // condemns that blob from the snapshots published so far; after its
        // uploads, a second pass finds the grace run.
        await File.WriteAllBytesAsync(ReturningPath, Returning);
        for (var index = 0; index < NewFileCount; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(SourceRoot, $"b-new-{index:d2}.bin"), RandomBytes(seed: 100 + index, NewFileLength));
        }

        var condemning = new PassAtFirstContentPut(() => RunAsync(Day1.AddDays(1).AddHours(1)));
        var sweeping = new PassAtStep(
            PublicationStep.UploadBlobs, () => RunAsync(Day1.AddDays(1).AddHours(2)).GetAwaiter().GetResult());
        var published = await BackUpThroughAsync(Day1.AddDays(2), sweeping, condemning.Over, smallBlobs: true);

        Assert.IsNotNull(condemning.Report, "the premise: a pass ran once the backup had built on day one's blob");
        Assert.IsLessThan(
            (long)(NewFileCount * NewFileLength) + Returning.Length,
            published.ContentBlobs.Sum(blob => blob.Length),
            "the premise: the backup built on day one's blob rather than storing the bytes again");
        Assert.Contains(
            line => line.StartsWith("held beside a backup:", StringComparison.Ordinal),
            condemning.Report.Lines,
            "a pass condemned a blob while a backup was in flight: " + string.Join(" | ", condemning.Report.Lines));

        await AssertRestoresAsync(published.SnapshotId);

        // Nothing is left looking like damage: the next quiet pass finds day
        // one's blob reachable, and no tombstone ever said otherwise.
        var quiet = await RunAsync(Day1.AddDays(2).AddHours(1));
        Assert.IsFalse(
            quiet.Lines.Any(line => line.Contains("damage", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, quiet.Lines));
    }

    [TestMethod]
    public async Task RetentionPass_ReadsTheGraceClockBeforeTheSnapshotsItRevalidatesAgainst()
    {
        // Specification 11 §3.2 step 3: an object is revalidated against a
        // snapshot set read after the generation that made it eligible. Read
        // the other way round, a snapshot published between the two readings
        // counts towards the grace and is missing from the world that decides.
        await TombstoneTheReturningBytesAsync();
        await File.WriteAllBytesAsync(ReturningPath, Interim);
        await BackUpAsync(Day1.AddDays(2));

        var counting = new CountingObjectStore(new LocalFileSystemObjectStore(RepoPath));
        await RunAsync(Day1.AddDays(2).AddHours(1), counting);

        var listings = counting.Listings.ToList();
        var clock = listings.IndexOf("journal/");
        var snapshots = listings.IndexOf("snapshots/");
        Assert.IsGreaterThanOrEqualTo(0, clock, "the pass never read the journal");
        Assert.IsGreaterThanOrEqualTo(0, snapshots, "the pass never read the snapshots");
        Assert.IsLessThan(snapshots, clock, "the pass read the snapshots before the clock: " + string.Join(", ", listings));
    }

    /// <summary>Day one stores the bytes, day two replaces them, and a pass tombstones the blob that held them.</summary>
    private async Task TombstoneTheReturningBytesAsync()
    {
        await File.WriteAllBytesAsync(ReturningPath, Returning);
        await BackUpAsync(Day1);
        await File.WriteAllBytesAsync(ReturningPath, Interim);
        await BackUpAsync(Day1.AddDays(1));

        var first = await RunAsync(Day1.AddDays(1).AddHours(1));
        Assert.IsGreaterThanOrEqualTo(
            2, first.TombstonesWritten, "the premise: day one and the blob only it reaches are tombstoned");
        Assert.AreEqual(0, first.Swept!.Deleted, "the premise: nothing is eligible before the next publication");
    }

    /// <summary>
    /// Runs one capture through the publication path the service runs, over
    /// the set's own archive, sequence and catalogue.
    /// </summary>
    private async Task<(ReadOnlyMemory<byte> SnapshotId, IReadOnlyList<ArchivedBlob> ContentBlobs)> BackUpThroughAsync(
        DateTimeOffset now,
        IPublicationObserver? observer = null,
        Func<IObjectStore, IObjectStore>? wrap = null,
        bool smallBlobs = false)
    {
        var plain = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(plain, PassphraseText, CancellationToken.None);
        var repository = opened.Repository;
        var repositoryIdHex = repository.RepositoryId.ToString();
        var state = LocalState.LoadOrCreate(StateDirectory);
        var generation =
            repository.CurrentDataGeneration.Value >= repository.CurrentMetadataGeneration.Value
                ? repository.CurrentDataGeneration
                : repository.CurrentMetadataGeneration;

        // Small blobs only where a backup must outgrow the blobs its intent
        // names; the segmentation stays the default, or nothing would match
        // what earlier backups stored.
        var policy = smallBlobs
            ? CapturePolicy.Default with
            {
                Concurrency = 1,
                BlobWriteProfile = BlobWriteProfile.LocalDefault with
                {
                    TargetSizeBytes = 256 * 1024,
                    MaximumSizeBytes = 512 * 1024,
                },
            }
            : CapturePolicy.Default;

        using var catalogue = CatalogueDb.Open(
            Path.Combine(StateDirectory, $"catalogue-{repositoryIdHex}.db"), repository.RepositoryId);
        var prior = catalogue.EnumerateSnapshots()[0];

        var orchestrator = new PublicationOrchestrator(
            policy,
            repository.RepositoryId,
            WriterId.FromBytes(state.WriterId),
            generation,
            repository.Keys,
            repository.Credential,
            wrap is null ? plain : wrap(plain),
            new WriterSequence(new FileSequenceStateStore(Path.Combine(StateDirectory, $"sequence-{repositoryIdHex}.txt"))),
            Path.Combine(StateDirectory, "spool", repositoryIdHex),
            repository.EffectiveFormatVersion,
            observer,
            catalogue);

        var snapshotId = RandomNumberGenerator.GetBytes(16);
        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = new LocalFileSystemSource(),
                Roots = [new ScanRoot(SourceRoot)],
                DeviceId = state.DeviceId,
                BackupSetId = Convert.FromHexString(SetId),
                SnapshotId = snapshotId,
                ParentSnapshots = [prior.SnapshotId],
                PriorSnapshotId = prior.SnapshotId,
                NowUnixMilliseconds = (ulong)now.ToUnixTimeMilliseconds(),
                Clock = () => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = generation.Value + 2,
                ClientVersion = "beside-a-backup-tests/1.0",
            },
            CancellationToken.None);

        return (snapshotId, published.ContentBlobs);
    }

    private async Task<RetentionReport> RunAsync(DateTimeOffset now, IObjectStore? store = null)
    {
        var plain = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(plain, PassphraseText, CancellationToken.None);
        var sync = DestinationSyncStore.Open(StateDirectory);
        return await RetentionRunner.RunAsync(
            store ?? plain, opened.Repository, Policy, [new SetDestinationReference { Ref = "vault" }],
            name => sync.Find(SetId, name), _ => TrimVerification.None,
            WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId), apply: true,
            (ulong)now.ToUnixTimeMilliseconds(), CancellationToken.None, reclaim: opened.Reclaim);
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    /// <summary>The snapshot restores whole, and its returning file comes back byte for byte.</summary>
    private async Task AssertRestoresAsync(ReadOnlyMemory<byte> snapshotId)
    {
        var target = Path.Combine(_root, "restored", Guid.NewGuid().ToString("n"));
        var restored = await RestoreAsync(Convert.ToHexStringLower(snapshotId.Span), target);
        Assert.AreEqual(0, restored.Failed, restored.Outcome);
        var found = Assert.ContainsSingle(Directory.GetFiles(target, "a-returning.bin", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(Returning, await File.ReadAllBytesAsync(found));
    }

    private async Task<ApiRestoreResult> RestoreAsync(string snapshotId, string outputDirectory)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = ArchivesRoot, StateDirectory = StateDirectory },
            CancellationToken.None);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var description = (ServiceDescriptionResult)await handler.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);
        var opened = await handler.ExecuteAsync(
            new OpenRestoreSourceCommand(
                "docs",
                Envelope: WriteOnlyInstallation.RestoreGrant(
                    StateDirectory, PassphraseText, description.RestoreGrantRecipient!)),
            CancellationToken.None);
        var source = opened as RestoreSourceOpenedResult
            ?? throw new InvalidOperationException($"restore source refused: {(opened as ServiceError)?.Message ?? opened.ToString()}");
        var result = await handler.ExecuteAsync(
            new RunRestoreCommand(snapshotId, null, outputDirectory, Source: source.SourceId), CancellationToken.None);

        return result as ApiRestoreResult
            ?? throw new InvalidOperationException($"restore refused: {result}");
    }

    private static byte[] RandomBytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Runs a pass once, when a publication completes the named step.</summary>
    private sealed class PassAtStep(PublicationStep step, Func<RetentionReport> pass) : IPublicationObserver
    {
        public RetentionReport? Report { get; private set; }

        public void AfterStep(PublicationStep completedStep)
        {
            if (completedStep == step && Report is null)
            {
                Report = pass();
            }
        }
    }

    /// <summary>
    /// Runs a pass once, as the backup puts its first new content blob: by
    /// then every file before it in the scan has been matched against what is
    /// stored.
    /// </summary>
    private sealed class PassAtFirstContentPut(Func<Task<RetentionReport>> pass)
    {
        public RetentionReport? Report { get; private set; }

        public IObjectStore Over(IObjectStore inner) => new Store(this, inner);

        private async ValueTask BeforePutAsync(ObjectKey key)
        {
            if (Report is null && key.Value.StartsWith("blobs/data/", StringComparison.Ordinal))
            {
                Report = await pass().ConfigureAwait(false);
            }
        }

        private sealed class Store(PassAtFirstContentPut owner, IObjectStore inner) : IObjectStore
        {
            public StoreCapabilities Capabilities => inner.Capabilities;

            public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
                inner.GetMetadataAsync(key, cancellationToken);

            public ValueTask<OpenReadResult> OpenReadAsync(
                ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
                inner.OpenReadAsync(key, range, cancellationToken);

            public async ValueTask<PutResult> PutAsync(
                ObjectKey key,
                Func<CancellationToken, ValueTask<Stream>> openContent,
                PutConditions conditions,
                CancellationToken cancellationToken)
            {
                await owner.BeforePutAsync(key).ConfigureAwait(false);
                return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
            }

            public async IAsyncEnumerable<ObjectEntry> ListAsync(
                ObjectPrefix prefix,
                ListOptions options,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return entry;
                }
            }

            public ValueTask<DeleteResult> DeleteAsync(
                ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
                inner.DeleteAsync(key, conditions, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
