using System.Net;
using System.Net.Sockets;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using RestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Adopting an archive from an S3-compatible bucket after the machine that
/// wrote it is gone (FR-DR-009, ADR-0061, ADR-0091 Amendment 1). Discovery
/// lists what the bucket's prefix holds from each descriptor alone; the
/// preview shows what the archive recorded; and adoption takes the set back
/// under its original id as a staging set, because a direct-ship run never
/// writes through a store. Its staging archive holds what a trimmed one
/// does — the archive's metadata and its newest backup's data — so the next
/// backup is incremental, and it is synced back to the bucket it came from.
/// </summary>
[TestClass]
public sealed class S3AdoptionTests : IAsyncDisposable
{
    private const string Bucket = "family-backups";
    private const string Prefix = "site-a";
    private const string Cloud = "cloud";
    private const string CloudId = "c10dc10dc10dc10dc10dc10dc10dc10d";

    private readonly HostHarness _harness = new();
    private readonly S3CompatibleTestServer _store = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    public S3AdoptionTests() => _store.CreateBucket(Bucket);

    private CancellationToken Timeout => _timeout.Token;

    private static string PassphraseText => "The hosts-tests Passphrase 42 of this installation!";

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Discover_ABucket_ListsTheArchiveUnderItsPrefix_FromItsDescriptorAlone()
    {
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        var repositoryId = await BackUpThenLoseTheMachineAsync();

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var writesBefore = Writes();

        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var discovered);
        var row = Assert.ContainsSingle(discovered.Archives);
        Assert.AreEqual(repositoryId, row.RepositoryId);
        Assert.IsNull(row.OwnedBySet);
        Assert.IsFalse(row.SameInstallation, "the rebuilt installation minted its own salt");
        Assert.AreEqual(1, row.SnapshotObjects);
        Assert.IsTrue(row.HighestPublicationSequence > 0);
        Assert.IsEmpty(discovered.Warnings);
        Assert.AreEqual(writesBefore, Writes(), "discovery reads and writes nothing");
    }

    [TestMethod]
    public async Task Adopt_FromABucket_ResumesTheSetUnderItsOriginalId_AsAStagingSetThatSyncsBackToIt()
    {
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        WriteIncompressible("docs/big.bin");
        var repositoryId = await BackUpThenLoseTheMachineAsync();
        var root = $"{Prefix}/{repositoryId}/";

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);

        var (preview, outcome) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync,
            new AdoptArchiveCommand(Cloud, row.RepositoryId, await EnvelopeForAsync(handler, row)),
            Timeout);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(outcome, out var adopted, "adoption refused");
        Assert.AreEqual(_harness.DocsSetId, adopted.SetId);
        Assert.AreEqual("docs", adopted.SetName);
        Assert.AreEqual(repositoryId, adopted.RepositoryId);
        Assert.IsTrue(adopted.WriterIdentityResumed, "a never-published installation resumes the archive's writer");
        Assert.IsTrue(
            preview.Lines.Any(line => line.Contains("staged", StringComparison.Ordinal)),
            "the preview says the set would be staged: " + string.Join(" | ", preview.Lines));
        Assert.IsTrue(
            adopted.Lines.Any(line => line.Contains("staged", StringComparison.Ordinal)),
            "the answer says the set is staged: " + string.Join(" | ", adopted.Lines));

        // A direct-ship run never writes through a store, so a set adopted
        // from one is staged, and the bucket is its destination.
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual(_harness.DocsSetId, set.Id);
        Assert.IsFalse(set.DirectShip);
        Assert.AreEqual(Cloud, Assert.ContainsSingle(set.Destinations).Ref);

        // The staging archive holds what a trimmed one does: every metadata
        // object, and the data the newest backup needs, which with one
        // backup is all of it.
        var staging = runtime.ArchivePath(_harness.DocsSetId);
        Assert.IsTrue(File.Exists(Path.Combine(staging, "repository-format")));
        Assert.AreEqual(
            Under(root + "blobs/meta/").Count, CountFiles(Path.Combine(staging, "blobs", "meta")),
            "every metadata blob is held");
        Assert.AreEqual(
            Under(root + "blobs/data/").Count, CountFiles(Path.Combine(staging, "blobs", "data")),
            "the newest backup's data is held");

        var ledger = runtime.DestinationSync.Find(_harness.DocsSetId, Cloud);
        Assert.IsNotNull(ledger?.BaselineCompletedAt, "the bucket is admitted without owing it a full copy");

        // The proof: change one file and touch the big one — the same bytes
        // with a new modification time, so it is read again — and only the
        // change reaches the bucket. Under a fresh writer identity, or with
        // the newest backup's data missing from staging, the big file would
        // be stored and sent whole again.
        var dataBefore = DataBytes(root);
        var heldBefore = _store.KeysIn(Bucket);
        _harness.WriteSourceFile("docs/notes.txt", "the second words");
        File.SetLastWriteTimeUtc(WriteIncompressible("docs/big.bin"), DateTime.UtcNow.AddMinutes(1));
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);

        var cloud = runtime.DestinationSync.Find(_harness.DocsSetId, Cloud)!;
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
        Assert.HasCount(2, Under(root + "snapshots/"), "the new backup reached the bucket beside the adopted one");
        Assert.IsTrue(
            _store.KeysIn(Bucket).All(key => key.StartsWith(root, StringComparison.Ordinal)),
            "a second archive was born beside the adopted one");
        Assert.IsTrue(
            heldBefore.All(_store.KeysIn(Bucket).Contains),
            "the sync after adoption took something the bucket held before it — staging is a cache, not the truth");
        var grown = DataBytes(root) - dataBefore;
        Assert.IsLessThan(64 * 1024, grown, $"the incremental sent {grown} bytes of data — it re-stored what it had");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest, null, output,
                    Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var notes = Assert.ContainsSingle(Directory.GetFiles(output, "notes.txt", SearchOption.AllDirectories));
        Assert.AreEqual("the second words", await File.ReadAllTextAsync(notes, Timeout));
    }

    [TestMethod]
    public async Task Adopt_FromABucket_StagesOnlyTheNewestBackupsData_AndAnOlderSnapshotRestoresFromTheBucket()
    {
        // What a trimmed staging archive holds (ADR-0034 §6): the newest
        // backup's data and not the history's. The history stays in the
        // bucket, and a restore of an older snapshot reads it there.
        _harness.WriteSourceFile("docs/notes.txt", "the first words");
        var old = File.ReadAllBytes(WriteIncompressible("docs/old.bin"));
        var repositoryId = await BackUpThenLoseTheMachineAsync(beforeASecondBackup: () =>
        {
            File.Delete(Path.Combine(_harness.SourceRoot, "docs", "old.bin"));
            _harness.WriteSourceFile("docs/notes.txt", "the second words");
        });
        var root = $"{Prefix}/{repositoryId}/";

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler, new AdoptArchiveCommand(Cloud, row.RepositoryId, await EnvelopeForAsync(handler, row))),
            out var adopted, "adoption refused");
        Assert.AreEqual(2, adopted.SnapshotCount);

        var staging = runtime.ArchivePath(_harness.DocsSetId);
        Assert.AreEqual(
            Under(root + "blobs/meta/").Count, CountFiles(Path.Combine(staging, "blobs", "meta")),
            "every metadata blob is held");
        var heldHere = CountFiles(Path.Combine(staging, "blobs", "data"));
        Assert.IsGreaterThan(0, heldHere, "the newest backup's data is held");
        Assert.IsLessThan(Under(root + "blobs/data/").Count, heldHere, "the data only the older backup needs stays in the bucket");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var oldest = listed.Snapshots.MinBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    oldest, null, output,
                    Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var back = Assert.ContainsSingle(Directory.GetFiles(output, "old.bin", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(old, await File.ReadAllBytesAsync(back, Timeout), "the older file came back from the bucket");
    }

    [TestMethod]
    public async Task Preview_AnArchiveTheBucketDoesNotHold_IsNotFound_BeforeAnyEnvelopeIsLookedAt()
    {
        // As a local path's missing directory: the archive must be there
        // before the envelope is opened, so the refusal names discovery.
        // The envelope here is not even hex, so a refusal from opening it
        // would say that instead.
        WriteConfiguration(withDocsSet: false);

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Cloud, new string('a', 32), "not hex"), Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason, refused.Message);
        Assert.Contains("run discovery", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Discover_ABucketWithNoAccessKeyStored_IsRefusedSayingHowToStoreOne()
    {
        WriteConfiguration(withDocsSet: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Failed, refused.Reason);
        Assert.Contains("no access key", refused.Message, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", refused.Message, StringComparison.Ordinal, "nothing was asked, so nothing refused");
        Assert.IsEmpty(_store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Discover_ABucketThatDoesNotAnswer_IsUnavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        WriteConfiguration(withDocsSet: false, endpoint: $"http://127.0.0.1:{closed}");

        await using var runtime = await StartAsync();
        await StoreAccessKeyAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Unavailable, refused.Reason, refused.Message);
        Assert.Contains(Cloud, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's sequence: set up, back the docs set up and sync it to the
    /// bucket, then lose the state directory and the archives — everything —
    /// and leave a configuration naming only the bucket. Returns the
    /// repository id the bucket holds.
    /// </summary>
    private async Task<string> BackUpThenLoseTheMachineAsync(Action? beforeASecondBackup = null)
    {
        WriteConfiguration(withDocsSet: true);
        await _harness.SetupAsync();
        string repositoryId;
        await using (var runtime = await ServiceRuntime.StartAsync(Options(), Timeout))
        {
            await StoreAccessKeyAsync(runtime);
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
            await pass.Transfers.WaitAsync(Timeout);
            if (beforeASecondBackup is not null)
            {
                beforeASecondBackup();
                pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddHours(2), Timeout);
                Assert.AreEqual(1, pass.Ran, "the second pass ran no backup");
                await pass.Transfers.WaitAsync(Timeout);
            }

            var cloud = runtime.DestinationSync.Find(_harness.DocsSetId, Cloud);
            Assert.AreEqual(DestinationSyncState.InSync, cloud?.State, cloud?.LastError);
            repositoryId = (await runtime.ExistingArchiveAsync(_harness.DocsSetId, Timeout))!.Repository.RepositoryId.ToString();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        Directory.CreateDirectory(_harness.StateDirectory);
        if (Directory.Exists(_harness.ArchivesRoot))
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
        }

        WriteConfiguration(withDocsSet: false);
        return repositoryId;
    }

    /// <summary>
    /// Starts the service on the rebuilt machine: setup runs again through
    /// the agent's own verb — a new salt under the same passphrase — because
    /// the harness's once-only guard remembers the installation that died.
    /// </summary>
    private async Task<ServiceRuntime> StartAsync()
    {
        var setup = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss",
            "--user", HostHarness.OwnerUser, "--password-env", _harness.PasswordVariable);
        Assert.IsTrue(setup.ExitCode == 0 || setup.All.Contains("already", StringComparison.OrdinalIgnoreCase), setup.All);
        return await ServiceRuntime.StartAsync(Options(), Timeout);
    }

    private ServiceOptions Options() => new()
    {
        ArchivesRoot = _harness.ArchivesRoot,
        StateDirectory = _harness.StateDirectory,
    };

    private void WriteConfiguration(bool withDocsSet, string? endpoint = null) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = CloudId,
                Name = Cloud,
                Kind = DestinationKind.S3,
                Endpoint = endpoint ?? _store.Endpoint.ToString(),
                Bucket = Bucket,
                Region = _store.Region,
                Prefix = Prefix,
            },
        ],
        BackupSets = withDocsSet
            ?
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = Cloud }],
                },
            ]
            : [],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    /// <summary>Stores the access key the way every client does: sealed to the service, where it was typed.</summary>
    private async Task StoreAccessKeyAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            Convert.FromHexString(description.RestoreGrantRecipient!), Cloud, _store.AccessKeyId, _store.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand(Cloud, _store.AccessKeyId, Convert.ToHexStringLower(envelope)), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    private async Task<DiscoveredArchiveDescriptor> DiscoverSingleAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var discovered);
        return Assert.ContainsSingle(discovered.Archives);
    }

    /// <summary>An adoption confirmed as the preview showed it, the only kind the contract takes (FR-DR-009).</summary>
    private async Task<ServiceResult> AdoptConfirmedAsync(ServiceCommandHandler handler, AdoptArchiveCommand adopt) =>
        (await HostHarness.PreviewThenAdoptAsync(handler.ExecuteAsync, adopt, Timeout)).Adopted;

    /// <summary>
    /// The client half of the ceremony: derive against the DISCOVERED salt
    /// and parameters, seal the write credential to the service's recipient
    /// key. The passphrase never reaches the service.
    /// </summary>
    private async Task<string> EnvelopeForAsync(ServiceCommandHandler handler, DiscoveredArchiveDescriptor row)
    {
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        var parameters = new Argon2Parameters
        {
            MemoryKiB = row.KdfMemoryKib, Iterations = row.KdfIterations, Parallelism = row.KdfParallelism,
        };
        using var passphrase = Passphrase.Create(PassphraseText);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, parameters, Convert.FromHexString(row.KdfSalt), KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority,
                Convert.FromHexString(row.KdfSalt), parameters));
    }

    /// <summary>300 KB that does not compress, the same bytes every call.</summary>
    private string WriteIncompressible(string relativePath)
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        var full = Path.Combine(_harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private List<string> Under(string prefix) =>
        [.. _store.KeysIn(Bucket).Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];

    private long DataBytes(string root) =>
        Under(root + "blobs/data/").Sum(key => (long)_store.ObjectIn(Bucket, key)!.Length);

    private int Writes() => _store.Requests.Count(request => request.Method is "PUT" or "DELETE" or "POST");

    private static int CountFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length : 0;
}
