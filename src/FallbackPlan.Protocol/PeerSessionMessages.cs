using System.Formats.Cbor;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>One message this protocol can put on the wire (specification peer-protocol 02 §5).</summary>
/// <remarks>
/// <see cref="BodyEntryCount"/> exists because deterministic CBOR admits no
/// indefinite-length map, so the count must be known before the first entry is
/// written rather than discovered as they go.
/// </remarks>
public interface IPeerMessage
{
    /// <summary>The type written at map key 0.</summary>
    PeerMessageType Type { get; }

    /// <summary>How many entries <see cref="WriteBody"/> writes, excluding the type.</summary>
    int BodyEntryCount { get; }

    /// <summary>Writes the body entries. The writer sorts keys, so order here is free.</summary>
    /// <param name="writer">The frame's writer.</param>
    void WriteBody(CborWriter writer);
}

/// <summary>
/// What each side sends the moment TLS completes, without waiting for the
/// peer's (specification peer-protocol 02 §3.1).
/// </summary>
/// <remarks>
/// This carries a claim, not a proof. Nothing may act on the identity here
/// until <see cref="SessionAuthProof"/> has been verified against the
/// channel-bound transcript — the whole point of 02 §2's state machine is that
/// there is a state in which a peer has said who it is and has not yet shown it.
/// </remarks>
/// <param name="Identity">This side's permanent peer identity.</param>
/// <param name="Nonce">Fresh random bytes for this connection.</param>
public sealed record SessionAuth(PeerIdentity Identity, ReadOnlyMemory<byte> Nonce) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.SessionAuth;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <summary>Builds this side's message with a fresh nonce.</summary>
    /// <param name="identity">This device's peer identity.</param>
    /// <returns>The message to send.</returns>
    public static SessionAuth Create(PeerIdentity identity) => new(identity, SessionBinding.Nonce());

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(Identity.PublicKey);
        writer.WriteInt32(2);
        writer.WriteByteString(Nonce.Span);
    }

    /// <summary>Reads the message from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The message.</returns>
    /// <exception cref="PeerProtocolException">The body violates 02 §3.1.</exception>
    public static SessionAuth Read(CborReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte[]? identity = null;
        byte[]? nonce = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    identity = reader.ReadByteString();
                    break;
                case 2:
                    nonce = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (identity is null || identity.Length != PeerIdentity.KeyLength
            || nonce is null || nonce.Length != SessionBinding.NonceLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "An authentication message is not the shape 02 §3.1 defines.");
        }

        return new SessionAuth(PeerIdentity.FromPublicKey(identity), nonce);
    }

    /// <summary>Whether two messages carry the same claim.</summary>
    /// <param name="other">The message to compare.</param>
    /// <returns><see langword="true"/> when identity and nonce match.</returns>
    public bool Equals(SessionAuth? other) =>
        other is not null && Identity == other.Identity && Nonce.Span.SequenceEqual(other.Nonce.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Identity, Nonce.Length);
}

/// <summary>
/// The signature that turns <see cref="SessionAuth"/>'s claim into a proof
/// (specification peer-protocol 02 §3.1).
/// </summary>
/// <param name="Signature">Ed25519 over the role-bound transcript of 02 §3.2.</param>
public sealed record SessionAuthProof(ReadOnlyMemory<byte> Signature) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.SessionAuthProof;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(Signature.Span);
    }

    /// <summary>Reads the proof from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="PeerProtocolException">The body violates 02 §3.1.</exception>
    public static SessionAuthProof Read(CborReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte[]? signature = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            if (key == 1)
            {
                signature = reader.ReadByteString();
            }
            else
            {
                reader.SkipValue();
            }
        });

        if (signature is null || signature.Length != PeerKeypair.SignatureLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"An authentication proof is {PeerKeypair.SignatureLength} bytes.");
        }

        return new SessionAuthProof(signature);
    }

    /// <summary>Whether two proofs are the same bytes.</summary>
    /// <param name="other">The proof to compare.</param>
    /// <returns><see langword="true"/> when the signatures match.</returns>
    public bool Equals(SessionAuthProof? other) =>
        other is not null && Signature.Span.SequenceEqual(other.Signature.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => Signature.Length;
}

/// <summary>
/// What each side says first (specification peer-protocol 02 §4).
/// </summary>
/// <param name="MinimumVersion">Lowest protocol version this side speaks.</param>
/// <param name="MaximumVersion">Highest protocol version this side speaks.</param>
/// <param name="FeaturesOffered">What this side supports.</param>
/// <param name="FeaturesRequired">What this side requires of the peer.</param>
/// <param name="AgentVersion">Informational. Nothing may branch on it (02 §2).</param>
/// <param name="Terms">Present when this side is the destination (01 §4).</param>
public sealed record SessionHello(
    ushort MinimumVersion,
    ushort MaximumVersion,
    IReadOnlyList<string> FeaturesOffered,
    IReadOnlyList<string> FeaturesRequired,
    string AgentVersion,
    PeerTerms? Terms) : IPeerMessage
{
    /// <summary>The most feature identifiers one hello may carry (00 §2.3).</summary>
    public const int MaximumFeatures = 256;

    /// <summary>The most bytes a hello body may occupy (00 §2.3).</summary>
    public const int MaximumBodyBytes = 65_536;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.SessionHello;

    /// <inheritdoc/>
    public int BodyEntryCount => Terms is null ? 5 : 6;

    /// <summary>Whether two hellos say the same thing.</summary>
    /// <param name="other">The hello to compare.</param>
    /// <returns><see langword="true"/> when every field matches.</returns>
    /// <remarks>
    /// Written out because the generated record equality compares the feature
    /// lists by reference, which would make a hello unequal to its own round
    /// trip. A wire message whose <c>==</c> depends on which object allocated
    /// the list is a message nothing should compare.
    /// </remarks>
    public bool Equals(SessionHello? other) =>
        other is not null
        && MinimumVersion == other.MinimumVersion
        && MaximumVersion == other.MaximumVersion
        && string.Equals(AgentVersion, other.AgentVersion, StringComparison.Ordinal)
        && Equals(Terms, other.Terms)
        && FeaturesOffered.SequenceEqual(other.FeaturesOffered, StringComparer.Ordinal)
        && FeaturesRequired.SequenceEqual(other.FeaturesRequired, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MinimumVersion);
        hash.Add(MaximumVersion);
        hash.Add(AgentVersion, StringComparer.Ordinal);
        hash.Add(Terms);
        foreach (var feature in FeaturesOffered.Concat(FeaturesRequired))
        {
            hash.Add(feature, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt32(MinimumVersion);
        writer.WriteInt32(2);
        writer.WriteUInt32(MaximumVersion);
        writer.WriteInt32(3);
        WriteFeatures(writer, FeaturesOffered);
        writer.WriteInt32(4);
        WriteFeatures(writer, FeaturesRequired);
        writer.WriteInt32(5);
        writer.WriteTextString(AgentVersion);

        if (Terms is not null)
        {
            writer.WriteInt32(6);
            writer.WriteStartMap(3);
            writer.WriteInt32(1);
            writer.WriteUInt64(Terms.QuotaBytes);
            writer.WriteInt32(2);
            writer.WriteTextString(Terms.ScheduleWindow);
            writer.WriteInt32(3);
            writer.WriteUInt32(Terms.RetentionFloorGenerations);
            writer.WriteEndMap();
        }
    }

    /// <summary>Reads a hello from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The hello.</returns>
    /// <exception cref="PeerProtocolException">The body violates 02 §2 or a 00 §2.3 limit.</exception>
    public static SessionHello Read(CborReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        ushort minimum = 0;
        ushort maximum = 0;
        var sawRange = false;
        IReadOnlyList<string> offered = [];
        IReadOnlyList<string> required = [];
        var agentVersion = string.Empty;
        PeerTerms? terms = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    minimum = PeerCbor.ReadUInt16(reader);
                    break;
                case 2:
                    maximum = PeerCbor.ReadUInt16(reader);
                    sawRange = true;
                    break;
                case 3:
                    offered = ReadFeatures(reader);
                    break;
                case 4:
                    required = ReadFeatures(reader);
                    break;
                case 5:
                    agentVersion = reader.ReadTextString();
                    break;
                case 6:
                    terms = ReadTerms(reader);
                    break;
                default:
                    // An unknown *key* inside a known message is skipped; an
                    // unknown message *type* is refused (02 §5). The difference
                    // is that here the shape is known, so a later version adding
                    // a field cannot be confused with a corrupted stream.
                    reader.SkipValue();
                    break;
            }
        });

        if (!sawRange || maximum < minimum)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A hello declared the version range {minimum}–{maximum}, which is not a range.");
        }

        return new SessionHello(minimum, maximum, offered, required, agentVersion, terms);
    }

    private static void WriteFeatures(CborWriter writer, IReadOnlyList<string> features)
    {
        if (features.Count > MaximumFeatures)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A hello of {features.Count} features exceeds the limit of {MaximumFeatures}.");
        }

        writer.WriteStartArray(features.Count);
        foreach (var feature in features)
        {
            writer.WriteTextString(feature);
        }

        writer.WriteEndArray();
    }

    private static List<string> ReadFeatures(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null || length > MaximumFeatures)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A hello declared more than the {MaximumFeatures} features this protocol permits.");
        }

        var features = new List<string>(length.Value);
        for (var i = 0; i < length; i++)
        {
            features.Add(ReadFeatureIdentifier(reader));
        }

        reader.ReadEndArray();
        return features;
    }

    private static string ReadFeatureIdentifier(CborReader reader)
    {
        var feature = reader.ReadTextString();

        // 02 §4: lower-case ASCII, non-empty. Refused on the way in rather than
        // normalised, because a feature name that two implementations spell
        // differently and one of them silently folds is a negotiation that
        // disagrees with itself while both sides believe they agree.
        if (feature.Length == 0)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A feature identifier is empty.");
        }

        foreach (var character in feature)
        {
            var permitted = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.';
            if (!permitted)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A feature identifier is not lower-case ASCII.");
            }
        }

        return feature;
    }

    private static PeerTerms ReadTerms(CborReader reader)
    {
        ulong quota = 0;
        var window = string.Empty;
        uint retentionFloor = 0;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    quota = reader.ReadUInt64();
                    break;
                case 2:
                    window = reader.ReadTextString();
                    break;
                case 3:
                    retentionFloor = reader.ReadUInt32();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return new PeerTerms(quota, window, retentionFloor);
    }
}

/// <summary>
/// What each side says once the other's hello is acceptable
/// (specification peer-protocol 02 §2).
/// </summary>
/// <param name="Version">The selected protocol version (02 §3).</param>
/// <param name="Features">The features in effect — the intersection (02 §4).</param>
public sealed record SessionAccept(ushort Version, IReadOnlyList<string> Features) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.SessionAccept;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <summary>Whether two acceptances say the same thing.</summary>
    /// <param name="other">The acceptance to compare.</param>
    /// <returns><see langword="true"/> when the version and features match.</returns>
    /// <remarks>See <see cref="SessionHello.Equals(SessionHello)"/> — same reason.</remarks>
    public bool Equals(SessionAccept? other) =>
        other is not null
        && Version == other.Version
        && Features.SequenceEqual(other.Features, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        foreach (var feature in Features)
        {
            hash.Add(feature, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt32(Version);
        writer.WriteInt32(2);
        writer.WriteStartArray(Features.Count);
        foreach (var feature in Features)
        {
            writer.WriteTextString(feature);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads an acceptance from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The acceptance.</returns>
    /// <exception cref="PeerProtocolException">The body violates 02 §2.</exception>
    public static SessionAccept Read(CborReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        ushort version = 0;
        var features = new List<string>();

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    version = PeerCbor.ReadUInt16(reader);
                    break;
                case 2:
                    var length = reader.ReadStartArray();
                    if (length is null || length > SessionHello.MaximumFeatures)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed, "An acceptance declared too many features.");
                    }

                    for (var i = 0; i < length; i++)
                    {
                        features.Add(reader.ReadTextString());
                    }

                    reader.ReadEndArray();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return new SessionAccept(version, features);
    }
}

/// <summary>
/// Why a side will not continue (specification peer-protocol 02 §6).
/// </summary>
/// <param name="Reason">The code a client branches on.</param>
/// <param name="Text">What a human reads. Nothing may parse it.</param>
public sealed record SessionRefuse(PeerRefusalReason Reason, string Text) : IPeerMessage
{
    /// <summary>The most bytes the human-readable text may occupy (02 §6).</summary>
    public const int MaximumTextBytes = 256;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.SessionRefuse;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <summary>Builds a refusal from an exception raised while serving a peer.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The refusal to send.</returns>
    /// <remarks>
    /// The text comes from the exception's message, which is written for the
    /// operator on <b>this</b> side; 02 §6 requires that a refusal served to a
    /// stranger disclose nothing, so it is truncated but never enriched.
    /// </remarks>
    public static SessionRefuse From(PeerProtocolException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SessionRefuse(exception.Reason, exception.Message);
    }

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt32((uint)Reason);
        writer.WriteInt32(2);
        writer.WriteTextString(Truncate(Text));
    }

    /// <summary>Reads a refusal from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="PeerProtocolException">The body violates 02 §6.</exception>
    public static SessionRefuse Read(CborReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        PeerRefusalReason reason = 0;
        var text = string.Empty;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    reason = (PeerRefusalReason)PeerCbor.ReadUInt16(reader);
                    break;
                case 2:
                    text = reader.ReadTextString();
                    if (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed,
                            $"A refusal's text exceeds {MaximumTextBytes} bytes.");
                    }

                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return new SessionRefuse(reason, text);
    }

    /// <summary>Trims the text to the limit on a UTF-8 boundary, never mid-character.</summary>
    private static string Truncate(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaximumTextBytes)
        {
            return text;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var length = MaximumTextBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}

/// <summary>Reading helpers shared by this protocol's message bodies.</summary>
internal static class PeerCbor
{
    /// <summary>Walks a map's remaining entries, handing each integer key to <paramref name="read"/>.</summary>
    /// <param name="reader">The reader, positioned inside a map.</param>
    /// <param name="read">Reads the value for a key, or skips it.</param>
    /// <remarks>
    /// A hand-written loop rather than a switch over a fixed key count, because
    /// the frame's outer map is already open and its entries are read one at a
    /// time — and because a message that gains a key in a later version must
    /// still parse here.
    /// </remarks>
    public static void ReadEntries(CborReader reader, Action<int> read)
    {
        var nested = reader.PeekState() == CborReaderState.StartMap;
        if (nested)
        {
            reader.ReadStartMap();
        }

        try
        {
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                read(reader.ReadInt32());
            }

            if (nested)
            {
                reader.ReadEndMap();
            }
        }
        catch (CborContentException exception)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A message body is not canonical CBOR.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A message body does not have the shape this protocol defines.", exception);
        }
    }

    /// <summary>Reads a <c>u16</c>, which CBOR does not have a distinct type for.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The value.</returns>
    /// <exception cref="PeerProtocolException">The value does not fit.</exception>
    public static ushort ReadUInt16(CborReader reader)
    {
        var value = reader.ReadUInt32();
        if (value > ushort.MaxValue)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"The value {value} does not fit the u16 this field is.");
        }

        return (ushort)value;
    }
}
