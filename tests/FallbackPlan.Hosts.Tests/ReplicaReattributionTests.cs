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
    private const string ClaimableIdHex = "fedcba9876543210fedcba9876543210";

    private CancellationToken Timeout => _timeout.Token;

    private ServiceRuntime? _runtime;
    private RemoteServiceListener? _listener;
    private ServiceCommandHandler? _local;
    private PeerKeypair? _serviceKeypair;
    private PeerGrantStore? _serviceGrants;
    private readonly List<PeerTlsConnection> _connections = [];
    private readonly List<PeerKeypair> _keypairs = [];
    private readonly Dictionary<string, string> _peerStates = new(StringComparer.Ordinal);

    [TestMethod]
    public async Task RePointingThroughTheService_IsWhatTheLiveListenerServes()
    {
        // A replica this destination took from a device that no longer
        // exists, attributed before any claim key was published — the exact
        // shape only an operator can rescue.
        var gone = await StartServiceAsync();

        // The rebuilt machine, paired here as any new peer would be, is
        // refused: the replica is somebody else's, as far as the ledger says.
        var rebuilt = PairPeer("rebuilt");
        var refused = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => OpenReplicaAsync(rebuilt));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, refused.Reason);

        // The operator's override, through the command surface — a prefix
        // of the fingerprint is enough, as it is for unpair.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, rebuilt.Identity.Fingerprint[..8])),
            out var changed);
        Assert.IsTrue(changed.Lines.Any(line => line.Contains("rebuilt", StringComparison.Ordinal)), string.Join("\n", changed.Lines));
        Assert.IsTrue(changed.Lines.Any(line => line.Contains(gone.Fingerprint, StringComparison.Ordinal)));

        // And the listener that has been serving since before the override
        // hands the replica to its new owner without a restart: one store,
        // shared, not two copies of one file.
        await OpenReplicaAsync(rebuilt);
        var old = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => OpenReplicaAsync(gone));
        Assert.AreEqual(PeerRefusalReason.TermsRefused, old.Reason);

        // Durable, and on the record: the ledger says so after a reopen, and
        // a notice that is never auto-resolved names the repository.
        Assert.AreEqual(
            rebuilt.Identity.Fingerprint,
            ReplicaOwnerStore.Open(_harness.StateDirectory).Find(RepositoryIdHex)!.Fingerprint);
        Assert.IsTrue(
            _runtime!.Notices.Unacknowledged.Any(notice => notice.Key == $"replica-reattributed:{RepositoryIdHex}"));
    }

    [TestMethod]
    public async Task AReplicaWhoseOwnerCanClaimIt_IsNotTheOperatorsToMove()
    {
        // The override exists for the replica the passphrase cannot reach,
        // and for no other: where a claim key is on record the owner proves
        // ownership under it, and an operator who could bypass that proof
        // would be the weakest path standing in for the strongest.
        var gone = await StartServiceAsync();
        Assert.IsTrue(_runtime!.ReplicaOwners.TryAttribute(ClaimableIdHex, gone.Fingerprint, claimPublicKey: new string('e', 64)));
        var rebuilt = PairPeer("rebuilt");

        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand(ClaimableIdHex, rebuilt.Identity.Fingerprint)), out var refused);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("claim", refused.Message, StringComparison.Ordinal);
        Assert.Contains("passphrase", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual(gone.Fingerprint, _runtime.ReplicaOwners.Find(ClaimableIdHex)!.Fingerprint);
    }

    [TestMethod]
    public async Task ADeviceWeStoreAt_CannotBeGivenAReplica()
    {
        // A replica belongs to a peer that stores here. A destination we
        // store AT holds nothing here and would gain a retrieval gate it
        // has no business behind.
        _ = await StartServiceAsync();
        var vault = PairPeer("vault", PeerRole.StoresForUs);

        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, vault.Identity.Fingerprint)), out var refused);

        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("stores here", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheRefusals_NameWhatIsWrong()
    {
        var gone = await StartServiceAsync();
        var rebuilt = PairPeer("rebuilt");

        // Not a repository id at all: the operator is told what one looks like.
        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand("docs", rebuilt.Identity.Fingerprint)), out var malformed);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, malformed.Reason);
        Assert.Contains("replicas", malformed.Message, StringComparison.Ordinal);

        // A well-formed id nobody stores here. The operator's own disk, so
        // there is no reconnaissance rule to keep and the answer is plain.
        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand(new string('9', 32), rebuilt.Identity.Fingerprint)), out var unknown);
        Assert.AreEqual(ServiceErrorReason.NotFound, unknown.Reason);

        // A device that is not paired here: pair it first.
        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, "ZZZZZZZZ")), out var unpaired);
        Assert.AreEqual(ServiceErrorReason.NotFound, unpaired.Reason);
        Assert.Contains("pair", unpaired.Message, StringComparison.Ordinal);

        // A prefix that matches two pairings is refused rather than guessed
        // — a fingerprint is a handle, never the identity (ADR-0030 §1).
        var twin = PairPeerSharingAPrefixWith(gone.Fingerprint);
        Assert.IsInstanceOfType<ServiceError>(
            await ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, gone.Fingerprint[..1])), out var ambiguous);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, ambiguous.Reason);
        Assert.Contains("more of the fingerprint", ambiguous.Message, StringComparison.Ordinal);
        Assert.AreNotEqual(twin.Identity.Fingerprint, _runtime!.ReplicaOwners.Find(RepositoryIdHex)!.Fingerprint);

        // The same owner again is acknowledged, not repeated.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, gone.Fingerprint)), out var same);
        Assert.IsTrue(same.Lines.Any(line => line.Contains("already", StringComparison.Ordinal)));
        Assert.IsEmpty(_runtime.Notices.Unacknowledged.Where(notice => notice.Key.StartsWith("replica-reattributed:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task APairedRemoteConsole_MayNeitherListNorRePoint()
    {
        // The operator's own machine, the operator's own authority (ADR-0053
        // §3): a paired console elsewhere may watch this service but not hand
        // out replicas it stores — the same line restart_service draws.
        _ = await StartServiceAsync();
        var rebuilt = PairPeer("rebuilt");
        var remote = new ServiceCommandHandler(
            _runtime!, RemoteBindingState.On(_listener!.Endpoint.ToString()), CallerScope.Remote);

        Assert.IsInstanceOfType<ServiceError>(
            await remote.ExecuteAsync(new ReattributeReplicaCommand(RepositoryIdHex, rebuilt.Identity.Fingerprint), Timeout),
            out var rePoint);
        Assert.IsInstanceOfType<ServiceError>(
            await remote.ExecuteAsync(new ListReplicaAttributionsCommand(), Timeout), out var listing);

        Assert.AreEqual(ServiceErrorReason.Refused, rePoint.Reason);
        Assert.AreEqual(ServiceErrorReason.Refused, listing.Reason);
        Assert.Contains("local", rePoint.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheListing_NamesTheOwnerAndWhetherThePassphraseCanMoveIt()
    {
        // What the console's table is built from: every replica stored here,
        // the label this device gave its owner, and whether a claim key is on
        // record — never the key itself.
        var gone = await StartServiceAsync();
        Assert.IsTrue(_runtime!.ReplicaOwners.TryAttribute(ClaimableIdHex, gone.Fingerprint, claimPublicKey: new string('e', 64)));

        Assert.IsInstanceOfType<ReplicaAttributionsResult>(
            await ExecuteAsync(new ListReplicaAttributionsCommand()), out var listed);

        Assert.HasCount(2, listed.Attributions);
        var plain = listed.Attributions.Single(row => row.RepositoryId == RepositoryIdHex);
        var claimable = listed.Attributions.Single(row => row.RepositoryId == ClaimableIdHex);
        Assert.AreEqual(gone.Fingerprint, plain.OwnerFingerprint);
        Assert.AreEqual("gone", plain.OwnerLabel);
        Assert.IsFalse(plain.Claimable);
        Assert.IsTrue(claimable.Claimable);
    }

    private async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command) =>
        await _local!.ExecuteAsync(command, Timeout);

    /// <summary>
    /// Starts the runtime and, over it, the remote binding exactly as the
    /// host wires them: the listener's replication state is the runtime's
    /// state directory, and the attribution store is the runtime's. One
    /// replica is attributed to a peer that stores here, with no claim key.
    /// </summary>
    /// <returns>The identity of that peer, which is about to be gone.</returns>
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
        var binding = RemoteBindingState.On(_listener.Endpoint.ToString());
        _listener.Bind(new ServiceCommandHandler(_runtime, binding, CallerScope.Remote));
        _local = new ServiceCommandHandler(_runtime, binding, CallerScope.Local);

        var gone = PairPeer("gone");
        Assert.IsTrue(_runtime.ReplicaOwners.TryAttribute(RepositoryIdHex, gone.Identity.Fingerprint));
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "replicas", RepositoryIdHex));
        return gone.Identity;
    }

    /// <summary>A peer pinned both ways.</summary>
    private PeerKeypair PairPeer(string label, PeerRole role = PeerRole.StoresHere)
    {
        var state = Path.Combine(_harness.WorkPath, label);
        Directory.CreateDirectory(state);
        var keypair = PeerKeypairStore.Open(state);
        _keypairs.Add(keypair);
        _peerStates[keypair.Identity.Fingerprint] = state;

        // Pinned on the instance the listener authenticates against: a grant
        // store is read once at open, as the claim drill's fixture does.
        _serviceGrants!.Pin(new PeerGrant(keypair.Identity, label, role, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(state).Pin(new PeerGrant(
            _serviceKeypair!.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));
        return keypair;
    }

    /// <summary>
    /// A peer whose fingerprint begins with the same character as
    /// <paramref name="fingerprint"/> — minted until one does, which a
    /// 32-symbol alphabet makes cheap.
    /// </summary>
    private PeerKeypair PairPeerSharingAPrefixWith(string fingerprint)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var state = Path.Combine(_harness.WorkPath, "twin", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(state);
            using (var candidate = PeerKeypairStore.Open(state))
            {
                if (candidate.Identity.Fingerprint[0] != fingerprint[0])
                {
                    continue;
                }
            }

            var keypair = PeerKeypairStore.Open(state);
            _keypairs.Add(keypair);
            _peerStates[keypair.Identity.Fingerprint] = state;
            _serviceGrants!.Pin(new PeerGrant(keypair.Identity, "twin", PeerRole.StoresHere, PeerTerms.None, PairedAt));
            return keypair;
        }

        throw new AssertFailedException("no keypair shared the first fingerprint symbol in 500 tries");
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
