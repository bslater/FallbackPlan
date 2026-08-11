using Bodu;
using System.Net;
using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Protocol;
using ProtocolIdentity = FallbackPlan.Protocol.PeerIdentity;

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
    private readonly Action<string>? _log;
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
        Action<string>? log)
    {
        _keypair = keypair;
        _grants = grants;
        _socket = socket;
        _agentVersion = agentVersion;
        _log = log;
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
    public static RemoteServiceListener Start(
        PeerKeypair keypair,
        PeerGrantStore grants,
        IPEndPoint endpoint,
        string agentVersion,
        Action<string>? log = null)
    {
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(endpoint);

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(endpoint);
            socket.Listen(backlog: 16);
            return new RemoteServiceListener(keypair, grants, socket, agentVersion, log);
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

    private async Task ServeAsync(Socket accepted)
    {
        try
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                accepted, DateTimeOffset.UtcNow, _stopping.Token).ConfigureAwait(false);

            PeerSession session;
            try
            {
                // The gate the local binding does not need: authenticate the
                // dialler as a pinned peer before a command can cross. An
                // unpaired or substituted client is refused here and never
                // reaches the service.
                session = await PeerSessionDriver.AcceptAsync(
                    connection, _keypair, _grants, _agentVersion, terms: null, _stopping.Token).ConfigureAwait(false);
            }
            catch (PeerProtocolException refusal)
            {
                _log?.Invoke($"remote connection refused: {refusal.Reason} — {refusal.Message}");
                return;
            }

            var peer = DescribePeer(session.Peer.Identity);
            _log?.Invoke($"remote peer authenticated: {peer}");

            // From here it is the same command contract the local binding runs;
            // the open TLS stream carries it (ADR-0030; peer-protocol 02 §9
            // withholds key material and plaintext, the payload is control).
            await ServiceConnectionPump.RunAsync(session.Stream, _service!, peer, _log, _stopping.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // A remote client that drops, or a handshake that fails at the TLS
            // layer, ends its own connection and nothing else.
            _log?.Invoke($"remote connection ended: {exception.Message}");
        }
    }

    private static string DescribePeer(ProtocolIdentity identity) => $"peer {identity.Fingerprint}";
}
