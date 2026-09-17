using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The operator's re-attribution
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// §3, FR-REP-001, FR-USR-004): a peer destination's operator points a
/// replica stored here at a different paired device, for the one replica the
/// passphrase-only claim cannot reach — one attributed before the claim key
/// existed, by a machine that died before any later offer could publish one.
/// </summary>
/// <remarks>
/// The attribution the listener consults has to be the attribution the
/// service re-points, or the override moves a file on disk while the live
/// retrieval gate goes on refusing from a copy it opened at start. So the
/// runtime owns the store and the listener borrows it — and the proof is a
/// retrieval, not a read of the file.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReplicaReattributionTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;
    private const string RepositoryIdHex = "0123456789abcdef0123456789abcdef";

    private CancellationToken Timeout => _timeout.Token;

    private ServiceRuntime? _runtime;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _serviceKeypair;
    private PeerGrantStore? _serviceGrants;
    private readonly List<PeerTlsConnection> _connections = [];
    private readonly List<PeerKeypair> _keypairs = [];
    private readonly Dictionary<string, string> _peerStates = new(StringComparer.Ordinal);

    [TestMethod]
    public async Task RePointingThroughTheRuntimesStore_IsWhatTheLiveListenerServes()
    {
        // A replica this destination took from a device that no longer
        // exists, attributed before any claim key was published — the exact
        // shape only an operator can rescue.
        var gone = await StartServiceAsync();
        Assert.IsTrue(_runtime!.ReplicaOwners.TryAttribute(RepositoryIdHex, gone.Fingerprint));
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "replicas", RepositoryIdHex));

        // The rebuilt machine, paired here as any new peer would be, is
        // refused: the replica is somebody else's, as far as the ledger says.
        var rebuilt = PairPeer("rebuilt");
        var refused = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => OpenReplicaAsync(rebuilt));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refused.Reason);

        // The operator's override, through the store the SERVICE holds.
        Assert.IsTrue(_runtime.ReplicaOwners.Reattribute(RepositoryIdHex, rebuilt.Identity.Fingerprint));

        // And the listener that has been serving since before the override
        // hands the replica to its new owner without a restart: one store,
        // shared, not two copies of one file.
        await OpenReplicaAsync(rebuilt);
        var old = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => OpenReplicaAsync(gone));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, old.Reason);
    }

    /// <summary>
    /// Starts the runtime and, over it, the remote binding exactly as the
    /// host wires them: the listener's replication state is the runtime's
    /// state directory, and the attribution store is the runtime's.
    /// </summary>
    /// <returns>The identity of a peer that stores here and is about to be gone.</returns>
    private async Task<Protocol.PeerIdentity> StartServiceAsync()
    {
        await _harness.SetupAsync();
        _runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);

        _serviceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        _serviceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        _listener = RemoteServiceListener.Start(
            _serviceKeypair, _serviceGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _harness.StateDirectory, owners: _runtime.ReplicaOwners);
        _listener.Bind(new ServiceCommandHandler(_runtime, RemoteBindingState.On(_listener.Endpoint.ToString())));

        var gone = PairPeer("gone");
        return gone.Identity;
    }

    /// <summary>A peer pinned both ways, storing here.</summary>
    private PeerKeypair PairPeer(string label)
    {
        var state = Path.Combine(_harness.WorkPath, label);
        Directory.CreateDirectory(state);
        var keypair = PeerKeypairStore.Open(state);
        _keypairs.Add(keypair);
        _peerStates[keypair.Identity.Fingerprint] = state;

        // Pinned on the instance the listener authenticates against: a grant
        // store is read once at open, as the claim drill's fixture does.
        _serviceGrants!.Pin(new PeerGrant(
            keypair.Identity, label, PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(state).Pin(new PeerGrant(
            _serviceKeypair!.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        return keypair;
    }

    private Task OpenReplicaAsync(Protocol.PeerIdentity identity) =>
        OpenReplicaAsync(_keypairs.Single(keypair => keypair.Identity.Fingerprint == identity.Fingerprint));

    /// <summary>Asks the destination to open the replica for reading (peer-protocol 07 §3).</summary>
    private async Task OpenReplicaAsync(PeerKeypair keypair)
    {
        var state = _peerStates[keypair.Identity.Fingerprint];
        var connection = await PeerTlsConnection.DialAsync(
            _listener!.Endpoint.Address.ToString(), _listener.Endpoint.Port, DateTimeOffset.UtcNow, Timeout);
        _connections.Add(connection);
        var session = await PeerSessionDriver.DialAsync(
            connection, keypair, PeerGrantStore.Open(state), _serviceKeypair!.Identity, "fallbackplan-agent",
            terms: null, requiredFeatures: null, cancellationToken: Timeout);

        await PeerFrame.WriteAsync(
            session.Stream,
            new RetrieveOpen(Convert.FromHexString(RepositoryIdHex), ReplicationInitiator.FormatCapability),
            Timeout);
        _ = await ReplicationWire.ReadAsync(
            session.Stream, PeerMessageType.RetrieveReady, RetrieveReady.Read, Timeout);
    }

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceKeypair?.Dispose();
        foreach (var keypair in _keypairs)
        {
            keypair.Dispose();
        }

        _timeout.Dispose();
        _harness.Dispose();
    }
}
