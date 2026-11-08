using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using RestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Adopting an archive from an object store after the machine that wrote it
/// is gone (FR-DR-009, ADR-0061, ADR-0091 Amendment 1, ADR-0093, ADR-0196
/// Amendment 2): every case here runs once for each store API the product
/// speaks. Discovery lists what the store's prefix holds from each
/// descriptor alone; the preview shows what the archive recorded; and
/// adoption takes the set back under its original id as a direct-ship set,
/// since a store that is all its set has is written through as each backup
/// runs. Only
/// the archive's metadata is copied here: the next backup is incremental
/// against the store, writes through it, and keeps no data on this machine,
/// and an older snapshot restores from the store.
/// </summary>
public abstract class ObjectStoreAdoptionTests : IAsyncDisposable
{
    /// <summary>The bucket or container the destination writes to.</summary>
    protected const string Namespace = "family-backups";

    /// <summary>Where in it the destination writes.</summary>
    protected const string Prefix = "site-a";

    /// <summary>The destination's name.</summary>
    protected const string Cloud = "cloud";

    /// <summary>The destination's id, which names its credential's file.</summary>
    protected const string CloudId = "c10dc10dc10dc10dc10dc10dc10dc10d";

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    /// <summary>Starts the suite against a store that already holds the bucket or container.</summary>
    /// <param name="store">The store, speaking the API the subclass is about.</param>
    protected ObjectStoreAdoptionTests(ObjectStoreTestServer store) => Store = store;

    /// <summary>The service's harness.</summary>
    protected HostHarness Harness { get; } = new();

    /// <summary>The store the destination names.</summary>
    protected ObjectStoreTestServer Store { get; }

    /// <summary>The test's deadline.</summary>
    protected CancellationToken Timeout => _timeout.Token;

    private static string PassphraseText => "The hosts-tests Passphrase 42 of this installation!";

    /// <summary>What a destination with no credential stored is told it lacks.</summary>
    protected abstract string MissingCredentialWords { get; }

    /// <summary>The endpoint a destination names for a store listening at <paramref name="origin"/>.</summary>
    /// <param name="origin">The store's scheme, host and port.</param>
    protected abstract string EndpointAt(Uri origin);

    /// <summary>The destination's declaration: its kind and its address.</summary>
    /// <param name="endpoint">Its endpoint.</param>
    protected abstract DestinationConfiguration DeclareStore(string endpoint);

    /// <summary>Stores the credential the way every client does: sealed to the service, where it was typed.</summary>
    /// <param name="runtime">The service.</param>
    protected abstract Task StoreCredentialAsync(ServiceRuntime runtime);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        _timeout.Dispose();
        Harness.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    public async Task Discover_AStore_ListsTheArchiveUnderItsPrefix_FromItsDescriptorAlone()
    {
        Harness.WriteSourceFile("docs/notes.txt", "the first words");
        var repositoryId = await BackUpThenLoseTheMachineAsync();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
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
    public async Task Adopt_FromAStore_ResumesTheSetUnderItsOriginalId_AsADirectShipSetThatWritesThroughIt()
    {
        Harness.WriteSourceFile("docs/notes.txt", "the first words");
        WriteIncompressible("docs/big.bin");
        var repositoryId = await BackUpThenLoseTheMachineAsync();
        var root = $"{Prefix}/{repositoryId}/";

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);

        var (preview, outcome) = await HostHarness.PreviewThenAdoptAsync(
            handler.ExecuteAsync,
            new AdoptArchiveCommand(Cloud, row.RepositoryId, await EnvelopeForAsync(handler, row)),
            Timeout);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(outcome, out var adopted, "adoption refused");
        Assert.AreEqual(Harness.DocsSetId, adopted.SetId);
        Assert.AreEqual("docs", adopted.SetName);
        Assert.AreEqual(repositoryId, adopted.RepositoryId);
        Assert.IsTrue(adopted.WriterIdentityResumed, "a never-published installation resumes the archive's writer");
        Assert.IsTrue(
            preview.Lines.Any(line => line.Contains("ships straight to it", StringComparison.Ordinal)),
            "the preview says the set would ship straight to the store: " + string.Join(" | ", preview.Lines));
        Assert.IsTrue(
            adopted.Lines.Any(line => line.Contains("ships straight to it", StringComparison.Ordinal)),
            "the answer says the set ships straight to the store: " + string.Join(" | ", adopted.Lines));
        Assert.IsFalse(
            adopted.Lines.Any(line => line.Contains("staged", StringComparison.Ordinal)),
            "nothing is staged: " + string.Join(" | ", adopted.Lines));

        // The store is all the set has, so it is written through (ADR-0196),
        // and the set is adopted direct-ship.
        var set = Assert.ContainsSingle(runtime.Configuration.BackupSets);
        Assert.AreEqual(Harness.DocsSetId, set.Id);
        Assert.IsTrue(set.DirectShip, "a set adopted from a store ships straight to it");
        Assert.AreEqual(Cloud, Assert.ContainsSingle(set.Destinations).Ref);

        // Only the metadata is copied here: no staging archive, no data. The
        // folder trees are metadata, so they are kept here as a set that
        // writes through its stores keeps them (ADR-0196 Amendment 3).
        Assert.IsFalse(Directory.Exists(runtime.ArchivePath(Harness.DocsSetId)), "no staging archive is made");
        Assert.IsTrue(
            Directory.EnumerateFiles(runtime.SetMetadataPath(Harness.DocsSetId), "*", SearchOption.AllDirectories).Any(),
            "the archive's metadata is copied into the set's metadata store");
        CollectionAssert.AreEquivalent(
            Under(root + "blobs/meta/").Select(key => key[root.Length..]).ToList(),
            MetadataBlobsHere(runtime),
            "each of the archive's folder trees is copied here");
        Assert.IsEmpty(DataBlobsHere(), "no data blob is copied to this machine");

        var ledger = runtime.DestinationSync.Find(Harness.DocsSetId, Cloud);
        Assert.IsNotNull(ledger?.BaselineCompletedAt, "the store is admitted without owing it a full copy");

        // The proof: change one file and touch the big one — the same bytes
        // with a new modification time, so it is read again — and only the
        // change reaches the store. Under a fresh writer identity, or with
        // the newest backup's data missing from staging, the big file would
        // be stored and sent whole again.
        var dataBefore = DataBytes(root);
        var heldBefore = Store.KeysIn(Namespace);
        Harness.WriteSourceFile("docs/notes.txt", "the second words");
        File.SetLastWriteTimeUtc(WriteIncompressible("docs/big.bin"), DateTime.UtcNow.AddMinutes(1));
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);

        var cloud = runtime.DestinationSync.Find(Harness.DocsSetId, Cloud)!;
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
        Assert.HasCount(2, Under(root + "snapshots/"), "the new backup reached the store beside the adopted one");
        Assert.IsTrue(
            Store.KeysIn(Namespace).All(key => key.StartsWith(root, StringComparison.Ordinal)),
            "a second archive was born beside the adopted one");
        Assert.IsTrue(
            heldBefore.All(Store.KeysIn(Namespace).Contains),
            "the backup after adoption took something the store held before it");
        var grown = DataBytes(root) - dataBefore;
        Assert.IsLessThan(64 * 1024, grown, $"the incremental sent {grown} bytes of data — it re-stored what it had");
        Assert.IsEmpty(DataBlobsHere(), "the backup wrote through the store and kept no data here");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var output = Path.Combine(Harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest, null, output,
                    Source: (await Harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var notes = Assert.ContainsSingle(Directory.GetFiles(output, "notes.txt", SearchOption.AllDirectories));
        Assert.AreEqual("the second words", await File.ReadAllTextAsync(notes, Timeout));

        // The trees a backup builds on are here: those copied at adoption,
        // and those the backup after it kept as it shipped them. The sync
        // after that backup reconciled the store by listing it and reading
        // it back, which is the store's to answer, so the pass measured is
        // the next one.
        Harness.WriteSourceFile("docs/notes.txt", "the third words");
        var mark = Store.Requests.Count;
        var next = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddHours(1), Timeout);
        Assert.AreEqual(1, next.Ran, "the pass ran no backup");
        await next.Transfers.WaitAsync(Timeout);
        var asked = Store.Requests.Skip(mark)
            .Select(request => $"{request.Method} {Uri.UnescapeDataString(request.Target)}")
            .ToList();
        Assert.IsFalse(
            asked.Any(request => request.StartsWith("GET ", StringComparison.Ordinal)
                && request.Contains("blobs/meta/", StringComparison.Ordinal)),
            $"a backup of the adopted set read a folder tree from the store: {string.Join(", ", asked)}");
    }

    [TestMethod]
    public async Task Adopt_FromAStore_KeepsNoDataHere_AndAnOlderSnapshotRestoresFromTheStore()
    {
        // Every backup's data stays in the store, the newest's too, and a
        // restore of an older snapshot reads it there.
        Harness.WriteSourceFile("docs/notes.txt", "the first words");
        var old = File.ReadAllBytes(WriteIncompressible("docs/old.bin"));
        var repositoryId = await BackUpThenLoseTheMachineAsync(beforeASecondBackup: () =>
        {
            File.Delete(Path.Combine(Harness.SourceRoot, "docs", "old.bin"));
            Harness.WriteSourceFile("docs/notes.txt", "the second words");
        });
        var root = $"{Prefix}/{repositoryId}/";

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var row = await DiscoverSingleAsync(handler);
        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await AdoptConfirmedAsync(
                handler, new AdoptArchiveCommand(Cloud, row.RepositoryId, await EnvelopeForAsync(handler, row))),
            out var adopted, "adoption refused");
        Assert.AreEqual(2, adopted.SnapshotCount);

        Assert.IsFalse(Directory.Exists(runtime.ArchivePath(Harness.DocsSetId)), "no staging archive is made");
        Assert.IsEmpty(DataBlobsHere(), "no backup's data is copied to this machine");
        Assert.IsNotEmpty(Under(root + "blobs/data/"), "the premise: the store holds the data");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var oldest = listed.Snapshots.MinBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var output = Path.Combine(Harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    oldest, null, output,
                    Source: (await Harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var back = Assert.ContainsSingle(Directory.GetFiles(output, "old.bin", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(old, await File.ReadAllBytesAsync(back, Timeout), "the older file came back from the store");
    }

    [TestMethod]
    public async Task Preview_AnArchiveTheStoreDoesNotHold_IsNotFound_BeforeAnyEnvelopeIsLookedAt()
    {
        // As a local path's missing directory: the archive must be there
        // before the envelope is opened, so the refusal names discovery.
        // The envelope here is not even hex, so a refusal from opening it
        // would say that instead.
        WriteConfiguration(withDocsSet: false);

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new PreviewAdoptionCommand(Cloud, new string('a', 32), "not hex"), Timeout),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason, refused.Message);
        Assert.Contains("run discovery", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Discover_AStoreWithNoCredentialStored_IsRefusedSayingHowToStoreOne()
    {
        WriteConfiguration(withDocsSet: false);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Failed, refused.Reason);
        Assert.Contains(MissingCredentialWords, refused.Message, StringComparison.Ordinal);
        Assert.Contains("destination-credentials", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", refused.Message, StringComparison.Ordinal, "nothing was asked, so nothing refused");
        Assert.IsEmpty(Store.Requests, "nothing is sent to a store the service cannot sign for");
    }

    [TestMethod]
    public async Task Discover_AStoreThatDoesNotAnswer_IsUnavailable()
    {
        WriteConfiguration(withDocsSet: false, endpoint: EndpointAt(new Uri($"http://127.0.0.1:{LoopbackPorts.Refused}")));

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Cloud), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Unavailable, refused.Reason, refused.Message);
        Assert.Contains(Cloud, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's sequence: set up, back the docs set up and sync it to the
    /// store, then lose the state directory and the archives — everything —
    /// and leave a configuration naming only the store. Returns the
    /// repository id the store holds.
    /// </summary>
    private async Task<string> BackUpThenLoseTheMachineAsync(Action? beforeASecondBackup = null)
    {
        WriteConfiguration(withDocsSet: true);
        await Harness.SetupAsync();
        string repositoryId;
        await using (var runtime = await ServiceRuntime.StartAsync(Options(), Timeout))
        {
            await StoreCredentialAsync(runtime);
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

            var cloud = runtime.DestinationSync.Find(Harness.DocsSetId, Cloud);
            Assert.AreEqual(DestinationSyncState.InSync, cloud?.State, cloud?.LastError);
            repositoryId = (await runtime.ExistingArchiveAsync(Harness.DocsSetId, Timeout))!.Repository.RepositoryId.ToString();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Harness.StateDirectory, recursive: true);
        Directory.CreateDirectory(Harness.StateDirectory);
        if (Directory.Exists(Harness.ArchivesRoot))
        {
            Directory.Delete(Harness.ArchivesRoot, recursive: true);
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
            "setup", "--archives", Harness.ArchivesRoot, "--state", Harness.StateDirectory,
            "--passphrase-env", Harness.PassphraseVariable, "--acknowledge-loss",
            "--user", HostHarness.OwnerUser, "--password-env", Harness.PasswordVariable);
        Assert.IsTrue(setup.ExitCode == 0 || setup.All.Contains("already", StringComparison.OrdinalIgnoreCase), setup.All);
        return await ServiceRuntime.StartAsync(Options(), Timeout);
    }

    private ServiceOptions Options() => new()
    {
        ArchivesRoot = Harness.ArchivesRoot,
        StateDirectory = Harness.StateDirectory,
    };

    private void WriteConfiguration(bool withDocsSet, string? endpoint = null) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations = [DeclareStore(endpoint ?? EndpointAt(Store.Origin))],
        BackupSets = withDocsSet
            ?
            [
                new BackupSetConfiguration
                {
                    Id = Harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = Harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = Cloud }],
                },
            ]
            : [],
    }.Save(Path.Combine(Harness.StateDirectory, "config.json"));

    /// <summary>The service's published recipient key, which every client seals a credential to.</summary>
    /// <param name="handler">The service's command handler.</param>
    protected async Task<byte[]> RecipientAsync(ServiceCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        return Convert.FromHexString(description.RestoreGrantRecipient!);
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
        var full = Path.Combine(Harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private List<string> Under(string prefix) =>
        [.. Store.KeysIn(Namespace).Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];

    private long DataBytes(string root) =>
        Under(root + "blobs/data/").Sum(key => (long)Store.ObjectIn(Namespace, key)!.Length);

    private int Writes() => Store.Requests.Count(request => request.Method is "PUT" or "DELETE" or "POST");

    /// <summary>Each metadata blob kept in the set's metadata store, by its key from <c>blobs/meta/</c> on.</summary>
    /// <param name="runtime">The service.</param>
    private List<string> MetadataBlobsHere(ServiceRuntime runtime) =>
        [.. Directory.EnumerateFiles(runtime.SetMetadataPath(Harness.DocsSetId), "*", SearchOption.AllDirectories)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => path.Contains("blobs/meta/", StringComparison.Ordinal))
            .Select(path => path[path.IndexOf("blobs/meta/", StringComparison.Ordinal)..])];

    /// <summary>Every data blob this machine holds, under its state and its archives.</summary>
    private List<string> DataBlobsHere() =>
        [.. new[] { Harness.StateDirectory, Harness.ArchivesRoot }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => path.Replace('\\', '/').Contains("blobs/data/", StringComparison.Ordinal))];
}
