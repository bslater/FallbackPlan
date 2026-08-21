using Bodu;
using System.Net;
using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Protocol;
using ProtocolIdentity = FallbackPlan.Protocol.PeerIdentity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// Hosts the service on the remote binding (ADR-0028 §5; ADR-0030): TLS over
/// TCP on a named interface, off until explicitly enabled, admitting only a
/// client whose device identity is pinned.
/// </summary>
/// <remarks>
/// The shape mirrors <see cref="LocalServiceListener"/> — accept, then serve
/// the command contract through <see cref="ServiceConnectionPump"/> — with one
/// difference that is the whole point of the binding: where the local listener
/// leans on the operating system to say who connected, this one runs the peer
/// session handshake (<see cref="PeerSessionDriver"/>) first, and a connection
/// that does not authenticate as a pinned peer never reaches a command. An
/// unpaired or substituted client is refused on the wire and its socket
/// closed, the service untouched.
/// </remarks>
public sealed class RemoteServiceListener : IAsyncDisposable
{
    private readonly PeerKeypair _keypair;
    private readonly PeerGrantStore _grants;
    private readonly Socket _socket;
    private readonly string _agentVersion;
    private readonly IReadOnlyList<string>? _offeredFeatures;
    private readonly ILogger _log;
    private readonly string? _replicasRoot;
    private readonly string? _spoolRoot;
    private readonly string? _stateDirectory;
    private readonly FallbackPlan.Application.ReplicaOwnerStore? _owners;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _gate = new();

    private IFallbackPlanService? _service;
    private Task? _acceptLoop;

    private RemoteServiceListener(
        PeerKeypair keypair,
        PeerGrantStore grants,
        Socket socket,
        string agentVersion,
        ILogger log,
        string? replicationStateDirectory,
        IReadOnlyList<string>? offeredFeatures)
    {
        _keypair = keypair;
        _grants = grants;
        _socket = socket;
        _agentVersion = agentVersion;
        _offeredFeatures = offeredFeatures;
        _log = log;
        _stateDirectory = replicationStateDirectory;
        if (replicationStateDirectory is not null)
        {
            // A peer that stores objects here writes them into a replica store
            // per source repository, chosen locally and never on the wire
            // (peer-protocol 03 §8).
            _replicasRoot = Path.Combine(replicationStateDirectory, "replicas");
            _spoolRoot = Path.Combine(replicationStateDirectory, "spool", "replication");
            _owners = FallbackPlan.Application.ReplicaOwnerStore.Open(replicationStateDirectory);
        }
    }

    /// <summary>The endpoint this listener is bound to, interface and port.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>
    /// Binds the socket on the validated options, exposing <see cref="Endpoint"/>
    /// but accepting nothing yet.
    /// </summary>
    /// <param name="keypair">This device's peer keypair, proving its identity.</param>
    /// <param name="grants">The pinned pairings; a client not here is refused.</param>
    /// <param name="endpoint">The interface and port to bind, already validated.</param>
    /// <param name="agentVersion">Informational build string for the session hello.</param>
    /// <param name="log">Optional sink for connection-level notes.</param>
    /// <returns>The bound listener; call <see cref="Bind"/> to begin serving, dispose to stop.</returns>
    /// <remarks>
    /// Binding and serving are two steps because the endpoint an OS-assigned
    /// port resolves to is what the service must report through
    /// <c>DescribeService</c> — and that service cannot be constructed until the
    /// endpoint is known. So the socket binds first, its endpoint seeds the
    /// service's binding state, and the service is handed back here.
    /// </remarks>
    /// <param name="replicationStateDirectory">
    /// Where a peer that stores objects here keeps its replicas and receive
    /// spool (03 §8). When null, an inbound peer whose grant permits storing
    /// here is refused, because the service has nowhere to put what it offers.
    /// </param>
    /// <param name="offeredFeatures">
    /// What this binding offers in its hello, defaulting to everything this
    /// build supports (see <see cref="PeerSessionNegotiation.Hello"/>).
    /// Narrowing it is how a compatibility test stands an older destination in
    /// front of a current source; nothing in production passes anything but
    /// the default.
    /// </param>
    public static RemoteServiceListener Start(
        PeerKeypair keypair,
        PeerGrantStore grants,
        IPEndPoint endpoint,
        string agentVersion,
        ILogger? log = null,
        string? replicationStateDirectory = null,
        IReadOnlyList<string>? offeredFeatures = null)
    {
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(endpoint);

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(endpoint);
            socket.Listen(backlog: 16);
            return new RemoteServiceListener(
                keypair, grants, socket, agentVersion, log ?? NullLogger.Instance,
                replicationStateDirectory, offeredFeatures);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Begins accepting and serving connections against <paramref name="service"/>.</summary>
    /// <param name="service">The service every authenticated peer commands.</param>
    /// <remarks>Called once, after the socket is bound and the service is built.</remarks>
    public void Bind(IFallbackPlanService service)
    {
        ThrowHelper.ThrowIfNull(service);

        if (_service is not null)
        {
            throw new InvalidOperationException("The remote listener is already serving.");
        }

        _service = service;
        _acceptLoop = AcceptAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();

        // Null when the socket bound but Bind was never called — a service that
        // failed to construct between the two steps. Nothing was ever served.
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopping the accept loop by disposing its socket is the
                // ordinary way it ends.
            }
        }

        Task[] outstanding;
        lock (_gate)
        {
            outstanding = [.. _connections];
        }

        await Task.WhenAll(outstanding).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _socket.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var connection = ServeAsync(accepted);
            lock (_gate)
            {
                _connections.RemoveAll(task => task.IsCompleted);
                _connections.Add(connection);
            }
        }
    }

    /// <summary>
    /// Serves one invite-authenticated pairing (01 §2.7). No human is prompted
    /// on this side: the operator approved when they issued the invite, and
    /// the dialler's MAC over the transcript and channel bindings is what
    /// proves it holds the code that issuance created.
    /// </summary>
    private async Task ServeInvitePairingAsync(PeerTlsConnection connection, System.Formats.Cbor.CborReader body)
    {
        if (_stateDirectory is null)
        {
            Log.PairingWithoutState(_log);
            return;
        }

        try
        {
            var offer = PairOffer.Read(body);
            var invites = PairingInviteStore.Open(_stateDirectory);
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var result = await PairingCeremony.AcceptWithInviteAsync(
                connection.Stream, offer, _keypair, _grants, Environment.MachineName, invites,
                initiatorBinding: connection.RemoteBinding, responderBinding: connection.LocalBinding,
                now, _stopping.Token).ConfigureAwait(false);

            if (result.Grant is { } grant)
            {
                Log.PeerPaired(_log, grant.Label, grant.Identity.Fingerprint);

                // Durable, not just a log line: the issuing operator learns who
                // redeemed their invite even if they were away (FR-DEST-008's
                // posture, applied to arrivals).
                FallbackPlan.Application.NoticeStore.Open(_stateDirectory).Raise(
                    $"pairing-invite-redeemed:{grant.Identity.Fingerprint}",
                    $"'{grant.Label}' ({grant.Identity.Fingerprint}) redeemed a pairing invite and is now "
                    + $"paired as {DescribeRole(grant.Role)}.",
                    now);
            }
            else
            {
                Log.PairingRefused(
                    _log, result.Refusal?.Reason.ToString() ?? "unknown", result.Refusal?.Text ?? string.Empty);
            }
        }
        catch (PeerProtocolException refusal)
        {
            Log.PairingRefused(_log, refusal.Reason.ToString(), refusal.Message);
        }
    }

    private static string DescribeRole(PeerRole role) => role switch
    {
        PeerRole.StoresHere => "a source whose backups store here",
        PeerRole.StoresForUs => "a destination that stores for this device",
        _ => "both a source and a destination",
    };

    private async Task ServeAsync(Socket accepted)
    {
        try
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                accepted, DateTimeOffset.UtcNow, _stopping.Token).ConfigureAwait(false);

            // The first frame decides what this connection is (ADR-0030
            // Amendment 3): a pairing offer redeems an invite; anything else
            // is the session handshake, handed on with the frame it opened
            // with. An unpaired dialler with no valid invite is refused either
            // way — by the ceremony there, by the authenticator here.
            var first = await PeerFrame.ReadAsync(connection.Stream, _stopping.Token).ConfigureAwait(false);
            if (first is null)
            {
                return;
            }

            if (first.Value.Type == PeerMessageType.PairOffer)
            {
                await ServeInvitePairingAsync(connection, first.Value.Body).ConfigureAwait(false);
                return;
            }

            PeerSession session;
            try
            {
                // The gate the local binding does not need: authenticate the
                // dialler as a pinned peer before a command can cross. An
                // unpaired or substituted client is refused here and never
                // reaches the service.
                // The hello carries this side's current terms for the peer it
                // authenticated as — the destination announcing what its own
                // grant will enforce (peer-protocol 05 §6). A console grant
                // stores nothing here, so it is offered none.
                session = await PeerSessionDriver.AcceptAsync(
                    connection, _keypair, _grants, _agentVersion, terms: null,
                    termsForPeer: grant => grant.Role is PeerRole.StoresHere or PeerRole.Both ? grant.Terms : null,
                    offeredFeatures: _offeredFeatures,
                    preread: first,
                    cancellationToken: _stopping.Token).ConfigureAwait(false);
            }
            catch (PeerProtocolException refusal)
            {
                Log.RemoteRefused(_log, refusal.Reason.ToString(), refusal.Message);
                return;
            }

            var peer = DescribePeer(session.Peer.Identity);
            Log.PeerAuthenticated(_log, peer);

            // The grant's role decides which payload the open stream carries
            // (peer-protocol 03 §1). A peer entitled to store objects here speaks
            // the replication payload; anything else — a console — speaks the
            // ADR-0028 command contract, exactly as the local binding does.
            if (session.Peer.Role is PeerRole.StoresHere or PeerRole.Both)
            {
                if (_replicasRoot is null)
                {
                    Log.ReplicationWithoutReplicas(_log, peer);
                    return;
                }

                // The first payload frame routes the session: an owner asking
                // to read its replica back speaks retrieval (peer-protocol
                // 07, ADR-0041); everything else is the replication payload,
                // handed its already-read first frame.
                var payload = await PeerFrame.ReadAsync(session.Stream, _stopping.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    return;
                }

                if (payload.Value.Type == PeerMessageType.RetrieveOpen
                    && session.Supports(PeerSessionNegotiation.RetrievalFeature))
                {
                    await RetrievalResponder.ServeAsync(
                        _replicasRoot, session.Stream, session.Peer, _owners!,
                        RetrieveOpen.Read(payload.Value.Body), _stopping.Token)
                        .ConfigureAwait(false);
                    Log.RetrievalServed(_log, peer);
                    return;
                }

                var outcome = await ReplicationResponder.ServeAsync(
                    _replicasRoot, _spoolRoot!, session.Stream, session.Peer, _owners!,
                    session.Supports(PeerSessionNegotiation.RetentionInstructionFeature),
                    session.Supports(PeerSessionNegotiation.DestinationVerificationFeature), _stopping.Token,
                    preread: payload)
                    .ConfigureAwait(false);

                if (outcome.Termination is { } termination)
                {
                    // The peering ended, said so, and both sides now know: the
                    // notice survives restarts until a human acknowledges it,
                    // and the grant goes — what this device already stores for
                    // that peer is its own to keep or evict on its own
                    // timetable (01 §3, ADR-0030 Amendment 2).
                    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    FallbackPlan.Application.NoticeStore.Open(_stateDirectory!).Raise(
                        $"peering-terminated:{session.Peer.Identity.Fingerprint}",
                        $"Peer '{session.Peer.Label}' ({session.Peer.Identity.Fingerprint}) ended the peering"
                        + $"{(termination.Reason.Length > 0 ? $": {termination.Reason}" : string.Empty)}. "
                        + $"Data stored for it here is yours to keep or evict"
                        + $"{(termination.GraceDays > 0 ? $"; it suggests a {termination.GraceDays}-day grace" : string.Empty)}.",
                        now);
                    _grants.Revoke(session.Peer.Identity);
                    Log.PeeringTerminated(_log, peer);
                    return;
                }

                Log.Replicated(_log, outcome.Committed, peer);
                return;
            }

            // The open TLS stream carries the command contract (ADR-0030;
            // peer-protocol 02 §9 withholds key material and plaintext).
            await ServiceConnectionPump.RunAsync(session.Stream, _service!, peer, _log, _stopping.Token)
                .ConfigureAwait(false);
        }
        catch (PeerProtocolException refusal)
        {
            // A replication exchange that violated the protocol was refused on
            // the wire by the responder; it ends this connection and no other.
            Log.RemoteRefused(_log, refusal.Reason.ToString(), refusal.Message);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // A remote client that drops, or a handshake that fails at the TLS
            // layer, ends its own connection and nothing else.
            Log.RemoteEnded(_log, exception.Message);
        }
    }

    private static string DescribePeer(ProtocolIdentity identity) => $"peer {identity.Fingerprint}";
}
