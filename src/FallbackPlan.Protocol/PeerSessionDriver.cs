using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// An authenticated, open session and the peer it belongs to
/// (specification peer-protocol 02 §2). The stream carries the payload the
/// console or a peer replication document defines; this type carries who is
/// on the far end and what version was agreed.
/// </summary>
public sealed class PeerSession
{
    internal PeerSession(Stream stream, PeerGrant peer, ushort version)
    {
        Stream = stream;
        Peer = peer;
        Version = version;
    }

    /// <summary>The open duplex stream. What flows over it now is the payload, not the handshake.</summary>
    public Stream Stream { get; }

    /// <summary>The grant the peer authenticated as (02 §3).</summary>
    public PeerGrant Peer { get; }

    /// <summary>The negotiated protocol version (02 §3).</summary>
    public ushort Version { get; }
}

/// <summary>
/// Drives one connection through the four states of specification peer-protocol
/// 02 §2 — <c>Connected</c> → <c>Encrypted</c> → <c>Authenticated</c> →
/// <c>Open</c> — over a real duplex stream, refusing and closing on any
/// violation.
/// </summary>
/// <remarks>
/// <para>
/// The pieces this composes each exist and are unit-tested in one process;
/// what this adds is the wire. The order is fixed by 02 §3.1: send this side's
/// <see cref="SessionAuth"/> at once without waiting, then take the peer's,
/// answer with the proof, and verify theirs — so authentication is one round
/// trip. Negotiation follows authentication deliberately (02 §3): a stranger
/// has no business learning what a device supports.
/// </para>
/// <para>
/// Every <see cref="PeerProtocolException"/> raised while serving becomes a
/// <see cref="SessionRefuse"/> on the wire before the connection closes (02
/// §6, §8) — a peer is told it was refused and told nothing more, because the
/// detail a refusal could carry is reconnaissance.
/// </para>
/// </remarks>
public static class PeerSessionDriver
{
    /// <summary>Accepts an inbound connection: authenticate the dialler, then open.</summary>
    /// <param name="connection">The TLS connection, its bindings already known.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The pinned pairings this device holds.</param>
    /// <param name="agentVersion">Informational build string for the hello.</param>
    /// <param name="terms">Terms to offer when this side is the destination (01 §4).</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The open session.</returns>
    /// <exception cref="PeerProtocolException">The peer was refused; a refusal was sent before closing.</exception>
    public static ValueTask<PeerSession> AcceptAsync(
        PeerTlsConnection connection,
        PeerKeypair keypair,
        PeerGrantStore grants,
        string agentVersion,
        PeerTerms? terms = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(connection, keypair, grants, expected: null, agentVersion, terms, cancellationToken);

    /// <summary>Dials a known peer: authenticate the one expected, then open.</summary>
    /// <param name="connection">The TLS connection, its bindings already known.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The pinned pairings this device holds.</param>
    /// <param name="expected">The peer this side dialled — an unexpected answer is refused hard (01 §2.5).</param>
    /// <param name="agentVersion">Informational build string for the hello.</param>
    /// <param name="terms">Terms to offer when this side is the destination (01 §4).</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The open session.</returns>
    /// <exception cref="PeerProtocolException">The peer was refused, or refused this side.</exception>
    public static ValueTask<PeerSession> DialAsync(
        PeerTlsConnection connection,
        PeerKeypair keypair,
        PeerGrantStore grants,
        PeerIdentity expected,
        string agentVersion,
        PeerTerms? terms = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(expected);
        return RunAsync(connection, keypair, grants, expected, agentVersion, terms, cancellationToken);
    }

    private static async ValueTask<PeerSession> RunAsync(
        PeerTlsConnection connection,
        PeerKeypair keypair,
        PeerGrantStore grants,
        PeerIdentity? expected,
        string agentVersion,
        PeerTerms? terms,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(connection);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(agentVersion);

        var stream = connection.Stream;
        var authenticator = new PeerAuthenticator(
            keypair, grants, connection.Role, connection.LocalBinding, connection.RemoteBinding, expected);

        try
        {
            // 02 §3.1: our claim goes out at once, without waiting for theirs.
            await PeerFrame.WriteAsync(stream, authenticator.Offer(), cancellationToken).ConfigureAwait(false);

            var theirAuth = await ReadAsync<SessionAuth>(
                stream, PeerMessageType.SessionAuth, authenticator, SessionAuth.Read, cancellationToken)
                .ConfigureAwait(false);
            var ourProof = authenticator.Accept(theirAuth);
            await PeerFrame.WriteAsync(stream, ourProof, cancellationToken).ConfigureAwait(false);

            var theirProof = await ReadAsync<SessionAuthProof>(
                stream, PeerMessageType.SessionAuthProof, authenticator, SessionAuthProof.Read, cancellationToken)
                .ConfigureAwait(false);
            var peer = authenticator.Verify(theirProof);

            // 02 §4: negotiation happens in the Authenticated state — the hello
            // and its answer are permitted there and nowhere else. Both sides
            // send a hello without waiting and compute the same answer from the
            // pair, so no acceptance need cross the wire (see
            // PeerSessionNegotiation). Only then does the session Open.
            var ourHello = PeerSessionNegotiation.Hello(agentVersion, terms);
            await PeerFrame.WriteAsync(stream, ourHello, cancellationToken).ConfigureAwait(false);

            var theirHello = await ReadAsync<SessionHello>(
                stream, PeerMessageType.SessionHello, authenticator, SessionHello.Read, cancellationToken)
                .ConfigureAwait(false);
            var accept = PeerSessionNegotiation.Negotiate(ourHello, theirHello);

            authenticator.Open();
            return new PeerSession(stream, peer, accept.Version);
        }
        catch (PeerProtocolException exception)
        {
            // Tell the peer it was refused and nothing more (02 §6, §8). A
            // refusal that cannot itself be sent — the connection is already
            // gone — is not worth masking the real failure with.
            await TryRefuseAsync(stream, exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        PeerMessageType expected,
        PeerAuthenticator authenticator,
        Func<System.Formats.Cbor.CborReader, T> read,
        CancellationToken cancellationToken)
    {
        var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"The peer closed before sending the expected {expected}.");

        var (type, body) = frame;

        if (type == PeerMessageType.SessionRefuse)
        {
            // The peer refused us. Surface its reason as our failure rather
            // than mislabelling it — this side did not violate anything.
            var refusal = SessionRefuse.Read(body);
            throw new PeerProtocolException(refusal.Reason, $"The peer refused: {refusal.Text}");
        }

        if (!Enum.IsDefined(type))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.MessageUnknown, $"The peer sent an unrecognised message type {(ushort)type}.");
        }

        if (!authenticator.Permits(type))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A {type} is not permitted in the {authenticator.State} state.");
        }

        if (type != expected)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Expected a {expected}; the peer sent a {type}.");
        }

        return read(body);
    }

    private static async ValueTask TryRefuseAsync(Stream stream, PeerProtocolException exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PeerFrame.WriteAsync(stream, SessionRefuse.From(exception), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception refusalFailure) when (refusalFailure is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer is gone or unresponsive. The original refusal is what
            // matters and is already propagating.
        }
    }
}
