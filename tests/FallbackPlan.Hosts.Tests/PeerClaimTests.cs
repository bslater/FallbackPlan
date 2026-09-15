using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The morning the machine is gone
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md),
/// FR-REP-001, FR-KIT-006). Does not establish FR-DEST-006.
/// </summary>
/// <remarks>
/// <para>
/// A replica is attributed to a <b>pinned peer identity</b> (peer-protocol
/// 05 §2), a device keypair is per-installation, and a rebuilt machine has a
/// new one. So the peer will not hand a rebuilt machine its own replica, and
/// re-pairing does not transfer the attribution — deliberately, because that
/// rule is what stops a stranger asking a peer for a repository by name.
/// </para>
/// <para>
/// Every existing peer drill misses this, because they all preserve the state
/// directory. Preserving the state directory is precisely what a dead machine
/// does not do, so this suite destroys it: the archive, the configuration, the
/// catalogue and the device keypair all go, and what is left is what a person
/// actually keeps — the recovery kit and the passphrase.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerClaimTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-claim", Guid.NewGuid().ToString("n"));

    private readonly string _rebuiltState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-claim-rebuilt", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;
    private Protocol.PeerIdentity? _destinationIdentity;

    /// <remarks>
    /// <b>This pins a gap, not a guarantee.</b> The refusal is correct for the
    /// rule it enforces and wrong for the person standing in front of it, and
    /// [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
    /// decided the claim ceremony that would let a rebuilt machine prove it is
    /// the same owner. Nothing implements that yet. When it does, this test
    /// inverts: the same drill ends with the replica opened and the backup
    /// restored, and the refusal below moves to the case it is really for — a
    /// stranger asking for a repository by name.
    /// </remarks>
    [TestMethod]
    public async Task ARebuiltMachine_PairedAfresh_IsStillRefusedItsOwnReplica()
    {
        var repositoryId = await SeedAsync();

        // The machine is gone: archive, state, keypair, configuration. What
        // survives is the kit and the passphrase, in a drawer somewhere.
        DestroyTheSourceMachine();

        // A fresh install, paired with the friend as any new peer would be —
        // ADR-0053 §2's first step, and the only step that works today.
        var rebuilt = PairRebuiltMachineAsync();

        // And it is refused its own repository, because the replica is
        // attributed to a device identity that no longer exists anywhere.
        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            () => OpenReplicaAsync(rebuilt, repositoryId));

        Assert.AreNotEqual(
            PeerRefusalReason.NotPaired,
            refusal.Reason,
            "the rebuilt machine IS paired — this must fail on attribution, or it proves nothing");
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refusal.Reason);
        Assert.Contains("retrievable under this pairing", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A backup to the peer, and the repository id it landed under.</summary>
    private async Task<byte[]> SeedAsync()
    {
        await StartDestinationAsync();
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", "the only copy, once the machine is gone");

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var replica = await ReplicaPathAsync();

        // The kit is exported while the machine still exists, which is the
        // only time anybody can export one.
        Directory.CreateDirectory(_harness.WorkPath);
        var exit = await Cli.CliApplication.RunAsync(
        [
            "key-export", "--output", Path.Combine(_harness.WorkPath, "kit.bin"),
            "--repo", Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId),
            "--passphrase-env", _harness.PassphraseVariable, "--state", _harness.StateDirectory,
        ]);
        Assert.AreEqual(0, exit);

        return Convert.FromHexString(Path.GetFileName(replica));
    }

    private void DestroyTheSourceMachine()
    {
        Directory.Delete(_harness.StateDirectory, recursive: true);
        if (Directory.Exists(_harness.ArchivesRoot))
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
        }
    }

    /// <summary>A fresh installation, pinned both ways with the friend.</summary>
    private PeerKeypair PairRebuiltMachineAsync()
    {
        Directory.CreateDirectory(_rebuiltState);
        var rebuilt = PeerKeypairStore.Open(_rebuiltState);

        _destinationGrants!.Pin(new PeerGrant(
            rebuilt.Identity, "rebuilt-source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_rebuiltState).Pin(new PeerGrant(
            _destinationIdentity!, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        return rebuilt;
    }

    /// <summary>Asks the peer to open the replica for reading (peer-protocol 07 §3).</summary>
    private async Task OpenReplicaAsync(PeerKeypair keypair, byte[] repositoryId)
    {
        var grants = PeerGrantStore.Open(_rebuiltState);
        await using var connection = await PeerTlsConnection.DialAsync(
            _endpoint!.Address.ToString(), _endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, grants, _destinationIdentity!, "fallbackplan-agent",
            terms: null, requiredFeatures: null, cancellationToken: Timeout);

        await PeerFrame.WriteAsync(
            session.Stream, new RetrieveOpen(repositoryId, ReplicationInitiator.FormatCapability), Timeout);
        _ = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetrieveReady, RetrieveReady.Read, Timeout);
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the destination never took a replica");
            await Task.Delay(100, Timeout);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();

        foreach (var directory in new[] { _destinationState, _rebuiltState })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A test directory that will not delete is not a test failure.
            }
        }
    }
}
