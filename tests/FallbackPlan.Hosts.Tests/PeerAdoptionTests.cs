using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The peer half of adoption (ADR-0061 §6; FR-WOR-006 over a peer): a
/// rebuilt machine pairs with the friend that holds its replica, claims it
/// with the passphrase (ADR-0053), and then discovers and adopts it through
/// the same verbs a local path takes — the peer's owner inventory is the
/// enumerator, the retrieval session is the store, and the next backup
/// ships incrementally to the peer. Does not establish FR-REP-001, which
/// the claim tests hold.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PeerAdoptionTests : IDisposable
{
    private const string Friend = "friend";
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-adopt", Guid.NewGuid().ToString("n"), "destination");
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;
    private Protocol.PeerIdentity? _destinationIdentity;

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_destinationState)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task ARebuiltMachine_ClaimsDiscoversAndAdoptsItsReplicaAtThePeer_AndResumesIncrementally()
    {
        // The friend, and a direct-ship set whose only destination it is.
        StartDestination();
        WriteConfiguration(withDocsSet: true);
        await _harness.SetupAsync();
        _harness.WriteSourceFile("docs/report.txt", "the only copy, once the machine is gone");
        WriteIncompressible("docs/big.bin");
        await using (var runtime = await StartRuntimeAsync())
        {
            var original = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, original, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }

        var replica = Assert.ContainsSingle(Directory.GetDirectories(Path.Combine(_destinationState, "replicas")));
        var repositoryId = Path.GetFileName(replica);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length);

        // The machine is gone. A fresh install, set up under the same
        // passphrase and a new salt, paired with the friend both ways, and
        // claiming its replica as the person would — with the verb.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        Directory.CreateDirectory(_harness.StateDirectory);
        var setup = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss",
            "--user", HostHarness.OwnerUser, "--password-env", _harness.PasswordVariable);
        Assert.AreEqual(0, setup.ExitCode, setup.All);
        PairRebuiltMachine();
        WriteConfiguration(withDocsSet: false);

        var claim = await HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new System.CommandLine.InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            "claim", $"{_endpoint!.Address}:{_endpoint.Port}",
            "--state", _harness.StateDirectory, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, claim.ExitCode, claim.All);

        await using var rebuilt = await StartRuntimeAsync();
        var handler = new ServiceCommandHandler(rebuilt, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Friend), Timeout), out var discovered,
            "discovery at the peer refused");
        var row = Assert.ContainsSingle(discovered.Archives);
        Assert.AreEqual(repositoryId, row.RepositoryId);
        Assert.IsNull(row.OwnedBySet);
        Assert.AreEqual(1, row.SnapshotObjects);

        var parameters = new Domain.Configuration.Argon2Parameters
        {
            MemoryKiB = row.KdfMemoryKib, Iterations = row.KdfIterations, Parallelism = row.KdfParallelism,
        };
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        string envelope;
        using (var passphrase = Repository.Crypto.Passphrase.Create(Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!))
        using (var authority = Repository.Crypto.WriteOnlyDerivation.Derive(
            passphrase, parameters, Convert.FromHexString(row.KdfSalt), Domain.Configuration.KdfValidationMode.OpenRepository))
        {
            envelope = Convert.ToHexStringLower(Repository.Crypto.WriteOnlyProvisioning.SealProvision(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority, Convert.FromHexString(row.KdfSalt), parameters));
        }

        Assert.IsInstanceOfType<ArchiveAdoptedResult>(
            await handler.ExecuteAsync(new AdoptArchiveCommand(Friend, repositoryId, envelope), Timeout),
            out var adopted, "adoption from the peer refused");
        Assert.AreEqual(_harness.DocsSetId, adopted.SetId);
        Assert.AreEqual("docs", adopted.SetName);
        Assert.AreEqual(_harness.SourceRoot, Assert.ContainsSingle(adopted.Roots).Path);
        Assert.IsTrue(adopted.WriterIdentityResumed);

        var set = Assert.ContainsSingle(rebuilt.Configuration.BackupSets);
        Assert.AreEqual(Friend, Assert.ContainsSingle(set.Destinations).Ref);
        Assert.IsTrue(set.DirectShip);
        Assert.IsFalse(
            Directory.Exists(Path.Combine(rebuilt.SetMetadataPath(_harness.DocsSetId), "blobs")),
            "content must never land locally");

        // The proof: touch the big file, change the small one, run — and the
        // peer's replica gains one snapshot and only kilobytes.
        var bytesBefore = ReplicaBytes(replica);
        _harness.WriteSourceFile("docs/report.txt", "the second words");
        File.SetLastWriteTimeUtc(WriteIncompressible("docs/big.bin"), DateTime.UtcNow.AddMinutes(1));
        var resumed = await Scheduler.Enqueue(rebuilt, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", resumed.Outcome, resumed.Detail);

        Assert.AreEqual(replica, Assert.ContainsSingle(Directory.GetDirectories(Path.Combine(_destinationState, "replicas"))));
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories).Length);
        var grown = ReplicaBytes(replica) - bytesBefore;
        Assert.IsTrue(grown < 64 * 1024, $"the resumed run shipped {grown} bytes to the peer");
    }

    [TestMethod]
    public async Task Discover_AtAPeerBeforeAnyClaim_ListsNothingRatherThanRefusing()
    {
        // Paired but never claimed: the inventory names nothing this device
        // owns, and an empty answer is the honest one — the remedy is the
        // claim, not a different verb.
        StartDestination();
        WriteConfiguration(withDocsSet: false);
        await _harness.SetupAsync();

        await using var runtime = await StartRuntimeAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ArchivesDiscoveredResult>(
            await handler.ExecuteAsync(new DiscoverArchivesCommand(Friend), Timeout), out var discovered);
        Assert.IsEmpty(discovered.Archives);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new AdoptArchiveCommand(Friend, new string('c', 32), "00"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
        Assert.Contains("claim", refused.Message, StringComparison.Ordinal);
    }

    private void StartDestination()
    {
        Directory.CreateDirectory(_destinationState);
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;
    }

    private void PairRebuiltMachine()
    {
        using var rebuilt = PeerKeypairStore.Open(_harness.StateDirectory);
        _destinationGrants!.Pin(new PeerGrant(rebuilt.Identity, "rebuilt-source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(_destinationIdentity!, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
    }

    private Task<ServiceRuntime> StartRuntimeAsync() =>
        ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout).AsTask();

    private void WriteConfiguration(bool withDocsSet) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = Friend,
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
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
                    Destinations = [new SetDestinationReference { Ref = Friend }],
                    DirectShip = true,
                },
            ]
            : [],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private string WriteIncompressible(string relativePath)
    {
        var bytes = new byte[300_000];
        new Random(11).NextBytes(bytes);
        var full = Path.Combine(_harness.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private static long ReplicaBytes(string replica) =>
        Directory.GetFiles(replica, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    /// <summary>The Bind contract needs a service; the replication and retrieval paths never call it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the peer's command surface is not part of this drill");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the peer's command surface is not part of this drill");
    }
}
