using Bodu;
using System.Buffers.Binary;
using System.Formats.Cbor;

namespace FallbackPlan.Protocol;

/// <summary>The message types this protocol defines (specification peer-protocol 02 §5).</summary>
public enum PeerMessageType : ushort
{
    /// <summary>Pairing offer (01 §2.2).</summary>
    PairOffer = 1,

    /// <summary>Pairing acceptance (01 §2.2).</summary>
    PairAccept = 2,

    /// <summary>Pairing confirmation (01 §2.2).</summary>
    PairConfirm = 3,

    /// <summary>Pairing refusal (01 §2.2).</summary>
    PairRefuse = 4,

    /// <summary>Session hello (02 §2).</summary>
    SessionHello = 5,

    /// <summary>Session acceptance (02 §2).</summary>
    SessionAccept = 6,

    /// <summary>Session refusal (02 §8).</summary>
    SessionRefuse = 7,

    /// <summary>Identity and nonce, sent without waiting (02 §3.1).</summary>
    SessionAuth = 8,

    /// <summary>The channel-bound signature (02 §3.1).</summary>
    SessionAuthProof = 9,

    /// <summary>A peer announces it has ended the peering (01 §3; feature-gated as "termination-notice").</summary>
    PeeringTermination = 10,

    /// <summary>A source's offer to replicate a repository (03 §3.1).</summary>
    ReplicationOffer = 256,

    /// <summary>A destination's inventory of what it already holds (03 §3.2).</summary>
    ReplicationInventory = 257,

    /// <summary>The start of one object's bytes (03 §3.3).</summary>
    ReplicationObject = 258,

    /// <summary>A slice of the current object's bytes (03 §3.3).</summary>
    ReplicationChunk = 259,

    /// <summary>The source has sent every object in scope (03 §3.4).</summary>
    ReplicationComplete = 260,

    /// <summary>The destination confirms what it received and committed (03 §3.4).</summary>
    ReplicationAck = 261,

    /// <summary>A commander's instruction naming the keys a replica drops (06 §4.1; feature-gated as "retention-instruction").</summary>
    RetentionOffer = 262,

    /// <summary>The spoke confirms what it deleted (06 §4.2).</summary>
    RetentionAck = 263,

    /// <summary>A keyed random-range challenge (04 §4.1).</summary>
    VerificationChallenge = 264,

    /// <summary>The destination's proof of possession, or its honest inability (04 §4.2).</summary>
    VerificationProof = 265,

    /// <summary>An owner asks to read back its replica (07 §3.1; feature-gated as "retrieval").</summary>
    RetrieveOpen = 272,

    /// <summary>The destination grants the retrieval session (07 §3.2).</summary>
    RetrieveReady = 273,

    /// <summary>A request for one page of the replica's listing (07 §3.3).</summary>
    RetrieveList = 274,

    /// <summary>One page of the listing (07 §3.3).</summary>
    RetrieveListPage = 275,

    /// <summary>A request for bytes of one object (07 §3.4).</summary>
    RetrieveRead = 276,

    /// <summary>The answer to one read (07 §3.4).</summary>
    RetrieveData = 277,

    /// <summary>A peer asks what it could claim here (07 §5.4; feature-gated as "replica-claim").</summary>
    ClaimRequest = 278,

    /// <summary>The destination's tokens and nonces for the replicas it could serve (07 §5.5).</summary>
    ClaimChallenge = 279,

    /// <summary>The claimant's signatures over those challenges (07 §5.6).</summary>
    ClaimProof = 280,

    /// <summary>What the claim moved, and the set ids each replica holds (07 §5.8).</summary>
    ClaimResult = 281,

    /// <summary>A source registers the public half of its claim credential (03 §3.2.1).</summary>
    ClaimRegister = 282,
}

/// <summary>Why a peer would not continue (specification peer-protocol 02 §6).</summary>
public enum PeerRefusalReason : ushort
{
    /// <summary>The presented key is not the pinned key.</summary>
    IdentityChanged = 1,

    /// <summary>No grant exists for the presented identity.</summary>
    NotPaired = 2,

    /// <summary>A grant existed and was revoked.</summary>
    Revoked = 3,

    /// <summary>No common protocol version.</summary>
    VersionUnsupported = 4,

    /// <summary>A required feature is absent.</summary>
    FeatureUnsupported = 5,

    /// <summary>An unrecognised message type.</summary>
    MessageUnknown = 6,

    /// <summary>A frame or body that violates the specification.</summary>
    Malformed = 7,

    /// <summary>The offered terms are unacceptable.</summary>
    TermsRefused = 8,

    /// <summary>The peer cannot serve a session now.</summary>
    Busy = 9,

    /// <summary>A human declined the pairing.</summary>
    PairingDeclined = 10,

    /// <summary>The peer did not prove possession of the identity it presented (02 §3.3).</summary>
    AuthenticationFailed = 11,

    /// <summary>The destination cannot store — disk trouble, not policy (05 §4).</summary>
    StorageExhausted = 12,

    /// <summary>
    /// The offered pairing invite is not redeemable here (01 §2.7). One code
    /// for unknown, expired and consumed alike — which of the three it was is
    /// nobody's business but the issuer's.
    /// </summary>
    InviteUnknown = 13,
}

/// <summary>
/// How far a session has got (specification peer-protocol 02 §2).
/// </summary>
/// <remarks>
/// Named and enforced rather than left implicit because
/// <see cref="Encrypted"/> is the state that looks finished and is not. An
/// implementation that treats a completed TLS handshake as an authenticated
/// session has a stranger inside the protocol, and every check after that point
/// is being applied to input it has no reason to believe.
/// </remarks>
public enum PeerSessionState
{
    /// <summary>Transport connected; nothing but a TLS handshake is permitted.</summary>
    Connected = 0,

    /// <summary>TLS complete. Encrypted, and authenticating nobody.</summary>
    Encrypted = 1,

    /// <summary>02 §3 satisfied in both directions.</summary>
    Authenticated = 2,

    /// <summary>02 §4 complete. The payload documents apply.</summary>
    Open = 3,
}

/// <summary>Raised when a peer sends something this protocol does not permit.</summary>
public sealed class PeerProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">The code to send back.</param>
    /// <param name="message">What to tell a human.</param>
    public PeerProtocolException(PeerRefusalReason reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="reason">The code to send back.</param>
    /// <param name="message">What to tell a human.</param>
    /// <param name="innerException">The cause.</param>
    public PeerProtocolException(PeerRefusalReason reason, string message, Exception innerException)
        : base(message, innerException) => Reason = reason;

    /// <summary>Creates the exception with no reason — present for the framework's benefit.</summary>
    public PeerProtocolException() => Reason = PeerRefusalReason.Malformed;

    /// <summary>Creates the exception with a message only.</summary>
    /// <param name="message">What to tell a human.</param>
    public PeerProtocolException(string message)
        : base(message) => Reason = PeerRefusalReason.Malformed;

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">What to tell a human.</param>
    /// <param name="innerException">The cause.</param>
    public PeerProtocolException(string message, Exception innerException)
        : base(message, innerException) => Reason = PeerRefusalReason.Malformed;

    /// <summary>The closed-set code a peer branches on (02 §6).</summary>
    public PeerRefusalReason Reason { get; }

    /// <summary>
    /// Whether this refusal arrived off the wire from the peer, as opposed to
    /// being raised here against the peer. A received refusal is surfaced and
    /// never echoed back as a refusal of its own (02 §6).
    /// </summary>
    public bool ReceivedFromPeer { get; init; }
}

/// <summary>
/// One message on the wire (specification peer-protocol 02 §5).
/// </summary>
/// <remarks>
/// <para>
/// A <c>u32</c> length prefix, then a deterministic CBOR map whose key 0 is the
/// message type. The length prefix sits outside any transport framing on
/// purpose: it bounds the allocation <b>before</b> any CBOR is parsed, and it
/// keeps the frame boundary a property of this protocol rather than of the
/// transport, which is what lets QUIC substitute for TCP without changing
/// anything above.
/// </para>
/// <para>
/// The bound is checked against the declared length, not against what arrived.
/// A parser that allocates from an unvalidated length read from an untrusted
/// peer is the standard shape of a denial-of-service bug, and this wire carries
/// input from parties the design explicitly does not trust (threat model T-7).
/// </para>
/// </remarks>
public static class PeerFrame
{
    /// <summary>The largest payload a frame may declare (00 §2.3).</summary>
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;

    /// <summary>The CBOR map key carrying the message type.</summary>
    public const int MessageTypeKey = 0;

    /// <summary>Writes one frame.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is written.</returns>
    /// <remarks>
    /// The map is written definite-length, which is why a message must declare
    /// its entry count rather than just writing entries. Deterministic encoding
    /// admits no indefinite-length item, so there is no shape here that lets a
    /// writer discover the count as it goes.
    /// </remarks>
    public static async ValueTask WriteAsync(
        Stream stream,
        IPeerMessage message,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(message);

        var payload = Encode(message);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or null when the peer closed cleanly.</summary>
    /// <param name="stream">Where to read from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The type and a reader positioned after the type entry, or null.</returns>
    /// <exception cref="PeerProtocolException">The frame violates this specification.</exception>
    public static async ValueTask<(PeerMessageType Type, CborReader Body)?> ReadAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);

        var prefix = new byte[sizeof(uint)];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(prefix);

        // Before the allocation, not after it.
        if (declared > MaximumPayloadBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"The peer declared a frame of {declared} bytes, over the {MaximumPayloadBytes}-byte limit.");
        }

        var payload = new byte[declared];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "The peer closed the connection part-way through a frame.");
        }

        return Decode(payload);
    }

    /// <summary>Encodes a message to the bytes a frame carries, without the length prefix.</summary>
    /// <param name="message">The message to encode.</param>
    /// <returns>The deterministic CBOR body.</returns>
    /// <exception cref="PeerProtocolException">The encoding exceeds the frame limit.</exception>
    public static byte[] Encode(IPeerMessage message)
    {
        ThrowHelper.ThrowIfNull(message);

        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(message.BodyEntryCount + 1);
        writer.WriteInt32(MessageTypeKey);
        writer.WriteUInt32((uint)message.Type);
        message.WriteBody(writer);
        writer.WriteEndMap();

        var payload = writer.Encode();
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A frame of {payload.Length} bytes exceeds the {MaximumPayloadBytes}-byte limit.");
        }

        return payload;
    }

    /// <summary>Reads a frame body already in memory — the read path without a stream.</summary>
    /// <param name="payload">The frame body.</param>
    /// <returns>The type and a reader positioned after the type entry.</returns>
    /// <exception cref="PeerProtocolException">The body violates this specification.</exception>
    public static (PeerMessageType Type, CborReader Body) Decode(byte[] payload)
    {
        ThrowHelper.ThrowIfNull(payload);

        var reader = new CborReader(payload, CborConformanceMode.Canonical);

        try
        {
            reader.ReadStartMap();
            if (reader.ReadInt32() != MessageTypeKey)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A frame's first map key is not the message type.");
            }

            return ((PeerMessageType)reader.ReadUInt32(), reader);
        }
        catch (CborContentException exception)
        {
            // Non-deterministic or malformed CBOR is refused rather than read
            // leniently: two encodings of one message is exactly the ambiguity
            // canonical encoding exists to remove.
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A frame's body is not canonical CBOR.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A frame's body does not have the shape this protocol defines.", exception);
        }
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}
