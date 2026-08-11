using System.Formats.Cbor;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The read-and-refuse discipline for the replication payload over an Open peer
/// session (specification peer-protocol 03), mirroring the session driver's own:
/// a peer's <c>SessionRefuse</c> surfaces as this side's failure, an unknown
/// type is <c>message_unknown</c>, a type not permitted once Open is
/// <c>malformed</c>, and every violation this side raises is answered with a
/// refusal before the stream closes.
/// </summary>
internal static class ReplicationWire
{
    /// <summary>Reads the next replication frame, applying the Open-state gates.</summary>
    public static async ValueTask<(PeerMessageType Type, CborReader Body)> ReadFrameAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "The peer closed part-way through the replication exchange.");

        var (type, body) = frame;

        if (type == PeerMessageType.SessionRefuse)
        {
            var refusal = SessionRefuse.Read(body);
            throw new PeerProtocolException(refusal.Reason, $"The peer refused: {refusal.Text}");
        }

        if (!Enum.IsDefined(type))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.MessageUnknown, $"The peer sent an unrecognised message type {(ushort)type}.");
        }

        if (!PeerAuthenticator.Permits(PeerSessionState.Open, type))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A {type} is not permitted once a session is open.");
        }

        return (type, body);
    }

    /// <summary>Reads the next frame and insists it is the expected type.</summary>
    public static async ValueTask<T> ReadAsync<T>(
        Stream stream, PeerMessageType expected, Func<CborReader, T> read, CancellationToken cancellationToken)
    {
        var (type, body) = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (type != expected)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Expected a {expected}; the peer sent a {type}.");
        }

        return read(body);
    }

    /// <summary>Sends a refusal for a raised violation, best effort, before closing.</summary>
    public static async ValueTask TryRefuseAsync(Stream stream, PeerProtocolException exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PeerFrame.WriteAsync(stream, SessionRefuse.From(exception), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception refusalFailure)
            when (refusalFailure is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer is gone or unresponsive; the original failure is what matters.
        }
    }
}
