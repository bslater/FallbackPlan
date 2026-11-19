using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Where a direct-ship set's sync copies from (FR-DEST-014). A sync copies
/// blobs before the snapshots that name them, so a snapshot it copies is one
/// whose blobs the destination holds, but only where its source lists every
/// blob. Outside a run a direct-ship set's archive reads the local paths, and
/// the stores where they are all it has, and never a peer. So a destination
/// whose missing history is held whole only at a peer is filled by reading
/// that history back from the peer; and one whose missing history is held
/// whole nowhere this machine can read is left behind, saying where it is
/// held, with nothing copied that it could not complete.
/// </summary>
/// <remarks>
/// Each case first ships a backup the way a run does, then syncs the lagging
/// destination, and asks of the destination what a restore would need: every
/// blob the complete copy holds. A destination holding the snapshots and the
/// index without their blobs restores nothing, however its status reads.
/// </remarks>
[TestClass]
public sealed class SyncSourceTests : IAsyncDisposable
{
    private const string Bucket = "family-backups";
    private const string StoreId = "c10dc10dc10dc10dc10dc10dc10dc10d";

    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-sync-source", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly S3CompatibleTestServer _store = new();
    private readonly List<TestPeer> _peers = [];

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string SecondVault => Path.Combine(_harness.WorkPath, "vault-two");

    [TestMethod]
    public async Task APeerAndAStore_TheStoreIsFilledFromThePeer_WithEveryBlobThePeerHolds()
    {
        // The run ships to the peer and leaves the store to the sync after
        // it. Nothing this machine reads outside a run holds the blobs, so
        // the sync reads them back from the peer.
        var friend = StartPeer("friend");
        WriteConfiguration(Peer(friend), Store());
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);

        var atThePeer = friend.Blobs();
        Assert.IsNotEmpty(atThePeer, "the premise: the run shipped blobs to the peer");
        var missing = atThePeer.Except(StoreBlobs(), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the store lacks blobs the peer holds: {string.Join(", ", missing)}");
        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.InSync, cloud.State, cloud.LastError);
    }

    [TestMethod]
    public async Task APeerAndALocalPath_ThePathAwayForTheFirstBackup_IsSeededFromThePeer()
    {
        var friend = StartPeer("friend");
        WriteConfiguration(Peer(friend), LocalPath("vault", Vault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        Assert.AreEqual(DestinationSyncState.Unavailable, Row(runtime, "vault").State, "the premise: the path was away");

        Directory.CreateDirectory(Vault);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await PassAsync(runtime, start.AddHours(1));

        var missing = friend.Blobs().Except(VaultBlobs(Vault), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the path lacks blobs the peer holds: {string.Join(", ", missing)}");
        var vault = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.InSync, vault.State, vault.LastError);
    }

    [TestMethod]
    public async Task APeerThatCannotBeRead_LeavesTheStoreBehind_SayingWhereTheBackupIs_AndCopiesNoSnapshotThere()
    {
        var friend = StartPeer("friend");
        WriteConfiguration(Peer(friend), Store());
        WriteFiles();

        await using var runtime = await StartAsync();
        await StoreCredentialAsync(runtime);
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
        Assert.IsNotEmpty(friend.Blobs(), "the premise: the run shipped to the peer");

        await friend.StopAsync();
        await SyncAsync(runtime, "cloud");

        var cloud = Row(runtime, "cloud");
        Assert.AreEqual(DestinationSyncState.Behind, cloud.State, cloud.LastError);
        Assert.Contains("'friend'", cloud.LastError!, StringComparison.Ordinal);
        var snapshots = _store.KeysIn(Bucket).Where(key => key.Contains("/snapshots/", StringComparison.Ordinal)).ToList();
        Assert.IsEmpty(snapshots, $"a snapshot reached the store without its blobs: {string.Join(", ", snapshots)}");
    }

    [TestMethod]
    public async Task TwoPeers_TheOneThatRefusedTheFirstBackup_IsFilledFromTheOther()
    {
        // The push to a peer reads the same source as the copy to a path or a
        // store: the peer that refused the first backup is seeded from the
        // one that took it.
        var friend = StartPeer("friend");
        var neighbour = StartPeer("neighbour", admitsUs: false);
        WriteConfiguration(Peer(friend), Peer(neighbour));
        WriteFiles();

        await using var runtime = await StartAsync();
        await PassAsync(runtime, DateTimeOffset.Now);
        Assert.IsEmpty(neighbour.Blobs(), "the premise: the second peer took nothing from the run");

        neighbour.Admit(_harness.StateDirectory);
        await SyncAsync(runtime, "neighbour");

        var missing = friend.Blobs().Except(neighbour.Blobs(), StringComparer.Ordinal).ToList();
        Assert.IsEmpty(missing, $"the second peer lacks blobs the first holds: {string.Join(", ", missing)}");
        var row = Row(runtime, "neighbour");
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
    }

    [TestMethod]
    public async Task TwoLocalPaths_TheOnlyWholeCopyUnplugged_LeaveTheOtherBehind_AndCopyItNoSnapshotItLacks()
    {
        // No peer at all: the second path took a backup the first missed,
        // and is away when the first comes back. The first is not handed
        // that backup's snapshot without its blobs.
        Directory.CreateDirectory(Vault);
        Directory.CreateDirectory(SecondVault);
        WriteConfiguration(LocalPath("vault", Vault), LocalPath("vault-two", SecondVault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime, "vault").State, "the premise: both paths took the first backup");

        var away = Vault + "-away";
        Directory.Move(Vault, away);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), start.AddHours(1), userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);

        Directory.Move(away, Vault);
        var secondAway = SecondVault + "-away";
        Directory.Move(SecondVault, secondAway);
        var snapshotsBefore = VaultSnapshots(Vault);
        await SyncAsync(runtime, "vault");

        var vault = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Behind, vault.State, vault.LastError);
        Assert.Contains("'vault-two'", vault.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(snapshotsBefore, VaultSnapshots(Vault), "the path was handed a snapshot whose blobs it lacks");
    }

    private static DestinationConfiguration Peer(TestPeer peer) => new()
    {
        Id = peer.Name == "friend" ? new string('1', 32) : new string('4', 32),
        Name = peer.Name,
        Kind = DestinationKind.Peer,
        Fingerprint = peer.Fingerprint,
        Endpoint = $"{peer.Endpoint.Address}:{peer.Endpoint.Port}",
        Priority = peer.Name == "friend" ? 5 : 3,
    };

    private DestinationConfiguration Store() => new()
    {
        Id = StoreId,
        Name = "cloud",
        Kind = DestinationKind.S3,
        Endpoint = _store.Origin.ToString(),
        Bucket = Bucket,
        Region = S3CompatibleTestServer.DefaultRegion,
        Prefix = "site-a",
    };

    private static DestinationConfiguration LocalPath(string name, string path) => new()
    {
        Id = name == "vault" ? new string('2', 32) : new string('3', 32),
        Name = name,
        Kind = DestinationKind.LocalPath,
        Path = path,
    };

    private void WriteConfiguration(params DestinationConfiguration[] destinations) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [.. destinations],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [.. destinations.Select(destination => new SetDestinationReference { Ref = destination.Name })],
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private void WriteFiles()
    {
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        _store.CreateBucket(Bucket);
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    /// <summary>
    /// Starts a peer listening that holds replicas for this installation, and
    /// pairs it here; it admits this installation now, or once told to.
    /// </summary>
    private TestPeer StartPeer(string name, bool admitsUs = true)
    {
        var peer = new TestPeer(name, Path.Combine(_peersRoot, name));
        _peers.Add(peer);
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(peer.Identity, name, PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        if (admitsUs)
        {
            peer.Admit(_harness.StateDirectory);
        }

        return peer;
    }

    private async Task StoreCredentialAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), Timeout), out var description);
        var envelope = WriteOnlyProvisioning.SealAccessKeySecret(
            Convert.FromHexString(description.RestoreGrantRecipient!), "cloud", _store.AccessKeyId, _store.SecretAccessKey);
        var stored = await handler.ExecuteAsync(
            new SetDestinationCredentialsCommand("cloud", _store.AccessKeyId, Convert.ToHexStringLower(envelope)), Timeout);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(stored, (stored as ServiceError)?.Message);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    private async Task SyncAsync(ServiceRuntime runtime, string destination)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(new SyncCommand("docs", destination), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime, string name) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, name) ?? throw new AssertFailedException($"no ledger row for '{name}'");

    private List<string> StoreBlobs() =>
        [.. _store.KeysIn(Bucket)
            .Where(key => key.Contains("/blobs/", StringComparison.Ordinal))
            .Select(key => key[(key.IndexOf("/blobs/", StringComparison.Ordinal) + 1)..])];

    private static List<string> VaultBlobs(string vault) =>
        Directory.Exists(vault) ? [.. Directory.GetDirectories(vault).SelectMany(BlobsUnder)] : [];

    private static int VaultSnapshots(string vault) =>
        Directory.GetDirectories(vault)
            .Select(replica => Path.Combine(replica, "snapshots"))
            .Where(Directory.Exists)
            .Sum(snapshots => Directory.GetFiles(snapshots, "*", SearchOption.AllDirectories).Length);

    private static IEnumerable<string> BlobsUnder(string replica)
    {
        var blobs = Path.Combine(replica, "blobs");
        return Directory.Exists(blobs)
            ? Directory.GetFiles(blobs, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(replica, path).Replace('\\', '/'))
            : [];
    }

    /// <summary>A replication peer on loopback, with its own state directory.</summary>
    private sealed class TestPeer : IAsyncDisposable
    {
        private readonly PeerKeypair _keypair;
        private readonly PeerGrantStore _grants;
        private RemoteServiceListener? _listener;

        public TestPeer(string name, string state)
        {
            Name = name;
            State = state;
            _keypair = PeerKeypairStore.Open(state);
            _grants = PeerGrantStore.Open(state);
            _listener = RemoteServiceListener.Start(
                _keypair, _grants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
                log: null, replicationStateDirectory: state);
            _listener.Bind(new UnusedService());
            Endpoint = _listener.Endpoint;
        }

        public string Name { get; }

        public string State { get; }

        public IPEndPoint Endpoint { get; }

        public Protocol.PeerIdentity Identity => _keypair.Identity;

        public string Fingerprint => _keypair.Identity.Fingerprint;

        /// <summary>Admits the installation whose state is at <paramref name="sourceState"/> to store here.</summary>
        public void Admit(string sourceState)
        {
            using var source = PeerKeypairStore.Open(sourceState);
            _grants.Pin(new PeerGrant(source.Identity, "source", PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
        }

        /// <summary>Each blob this peer holds for the installation, by its key from <c>blobs/</c> on.</summary>
        public List<string> Blobs()
        {
            var replicas = Path.Combine(State, "replicas");
            return Directory.Exists(replicas)
                ? [.. Directory.GetDirectories(replicas).SelectMany(BlobsUnder)]
                : [];
        }

        public async Task StopAsync()
        {
            if (_listener is { } listener)
            {
                _listener = null;
                await listener.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _keypair.Dispose();
        }
    }

    /// <summary>The Bind contract needs a service; a replication peer never reaches it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers)
        {
            await peer.DisposeAsync();
        }

        await _store.DisposeAsync();
        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            if (Directory.Exists(_peersRoot))
            {
                Directory.Delete(_peersRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
