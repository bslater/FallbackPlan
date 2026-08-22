using Bodu;
using System.Formats.Cbor;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>
/// The offer that opens a pairing (specification peer-protocol 01 §2.2).
/// </summary>
/// <remarks>
/// Everything here is public by design and none of it is trusted yet. The
/// ceremony's whole point is that these values are exchanged over a channel
/// nobody has authenticated, and that the two humans comparing short strings
/// afterwards is what makes them meaningful. A reader that acts on an offer
/// before <see cref="PairConfirm"/> has been checked has skipped the ceremony.
/// </remarks>
/// <param name="Contribution">The offerer's identity, ephemeral share and nonce.</param>
/// <param name="Label">Human-chosen, for display only, carrying no authority.</param>
/// <param name="HighestVersion">The highest protocol version the offerer speaks.</param>
/// <param name="RoleForResponder">The role the offerer will record for the responder (01 §2.2 key 7) — declared on the wire so both grants are approved together (ADR-0030 Amendment 2).</param>
/// <param name="InviteId">
/// The invite this offer redeems (01 §2.7, key 8) — the public lookup handle,
/// never the secret. Absent in the human-compared ceremony. It is a claim like
/// everything else here: the invite MAC on the confirmations is what proves it.
/// </param>
public sealed record PairOffer(
    PairingContribution Contribution,
    string Label,
    ushort HighestVersion,
    PeerRole RoleForResponder,
    ReadOnlyMemory<byte>? InviteId = null) : IPeerMessage
{
    /// <summary>The most bytes a human-chosen label may occupy (00 §2.3).</summary>
    public const int MaximumLabelBytes = 256;

    /// <summary>The most bytes a pairing message body may occupy (00 §2.3).</summary>
    public const int MaximumBodyBytes = 4_096;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.PairOffer;

    /// <inheritdoc/>
    public int BodyEntryCount => InviteId is null ? 6 : 7;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        PairingCbor.WriteContribution(writer, Contribution);
        writer.WriteInt32(4);
        writer.WriteTextString(PairingCbor.CheckedLabel(Label));
        writer.WriteInt32(5);
        writer.WriteUInt32(HighestVersion);
        writer.WriteInt32(7);
        writer.WriteUInt32((byte)RoleForResponder);

        if (InviteId is { } inviteId)
        {
            writer.WriteInt32(8);
            writer.WriteByteString(inviteId.Span);
        }
    }

    /// <summary>Reads an offer from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The offer.</returns>
    /// <exception cref="PeerProtocolException">The body violates 01 §2.2.</exception>
    public static PairOffer Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        var fields = PairingCbor.ReadFields(reader, hasTerms: false);
        return new PairOffer(
            fields.Contribution(), fields.Label, fields.Version, fields.CheckedRole(), fields.CheckedInviteId());
    }

    /// <summary>Whether two offers say the same thing.</summary>
    /// <param name="other">The offer to compare.</param>
    /// <returns><see langword="true"/> when every field matches.</returns>
    public bool Equals(PairOffer? other) =>
        other is not null
        && PairingCbor.SameContribution(Contribution, other.Contribution)
        && string.Equals(Label, other.Label, StringComparison.Ordinal)
        && HighestVersion == other.HighestVersion
        && RoleForResponder == other.RoleForResponder
        && (InviteId, other.InviteId) switch
        {
            (null, null) => true,
            ({ } ours, { } theirs) => ours.Span.SequenceEqual(theirs.Span),
            _ => false,
        };

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Contribution.Identity, Label, HighestVersion, RoleForResponder);
}

/// <summary>
/// The response that completes the key agreement (specification peer-protocol 01 §2.2).
/// </summary>
/// <param name="Contribution">The responder's identity, ephemeral share and nonce.</param>
/// <param name="Label">Human-chosen, for display only.</param>
/// <param name="SelectedVersion">The version chosen, never above the offerer's.</param>
/// <param name="Terms">Present when the responder is the destination (01 §4).</param>
/// <param name="RoleForOfferer">The role the responder will record for the offerer (01 §2.2 key 7).</param>
public sealed record PairAccept(
    PairingContribution Contribution,
    string Label,
    ushort SelectedVersion,
    PeerTerms? Terms,
    PeerRole RoleForOfferer) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.PairAccept;

    /// <inheritdoc/>
    public int BodyEntryCount => Terms is null ? 6 : 7;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        PairingCbor.WriteContribution(writer, Contribution);
        writer.WriteInt32(4);
        writer.WriteTextString(PairingCbor.CheckedLabel(Label));
        writer.WriteInt32(5);
        writer.WriteUInt32(SelectedVersion);

        if (Terms is not null)
        {
            writer.WriteInt32(6);
            PairingCbor.WriteTerms(writer, Terms);
        }

        writer.WriteInt32(7);
        writer.WriteUInt32((byte)RoleForOfferer);
    }

    /// <summary>Reads an acceptance from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The acceptance.</returns>
    /// <exception cref="PeerProtocolException">The body violates 01 §2.2.</exception>
    public static PairAccept Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        var fields = PairingCbor.ReadFields(reader, hasTerms: true);
        return new PairAccept(fields.Contribution(), fields.Label, fields.Version, fields.Terms, fields.CheckedRole());
    }

    /// <summary>Whether two acceptances say the same thing.</summary>
    /// <param name="other">The acceptance to compare.</param>
    /// <returns><see langword="true"/> when every field matches.</returns>
    public bool Equals(PairAccept? other) =>
        other is not null
        && PairingCbor.SameContribution(Contribution, other.Contribution)
        && string.Equals(Label, other.Label, StringComparison.Ordinal)
        && SelectedVersion == other.SelectedVersion
        && Equals(Terms, other.Terms);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Contribution.Identity, Label, SelectedVersion, Terms);
}

/// <summary>
/// One side's signature over the pairing transcript, sent after its human
/// approves (specification peer-protocol 01 §2.2).
/// </summary>
/// <remarks>
/// This is the only message in the ceremony that proves anything. The offer and
/// the acceptance are claims; this is the claim signed under a key whose
/// fingerprint a human has just read aloud and matched.
/// </remarks>
/// <param name="Signature">Ed25519 over <see cref="PairingTranscript.ConfirmationBytes"/>.</param>
/// <param name="InviteMac">
/// The invite proof (01 §2.7, key 2): HMAC-SHA-256 under the code-derived key
/// over the transcript and the connection's channel bindings. Present exactly
/// when the ceremony is invite-authenticated — it is what stands in for the
/// humans' string comparison, and covering the bindings is what makes a relay
/// fail the way a mismatched string does.
/// </param>
public sealed record PairConfirm(
    ReadOnlyMemory<byte> Signature, ReadOnlyMemory<byte>? InviteMac = null) : IPeerMessage
{
    /// <summary>The invite MAC's length: HMAC-SHA-256.</summary>
    public const int InviteMacLength = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.PairConfirm;

    /// <inheritdoc/>
    public int BodyEntryCount => InviteMac is null ? 1 : 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(Signature.Span);

        if (InviteMac is { } mac)
        {
            writer.WriteInt32(2);
            writer.WriteByteString(mac.Span);
        }
    }

    /// <summary>Reads a confirmation from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The confirmation.</returns>
    /// <exception cref="PeerProtocolException">The body violates 01 §2.2.</exception>
    public static PairConfirm Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? signature = null;
        byte[]? inviteMac = null;
        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    signature = reader.ReadByteString();
                    break;
                case 2:
                    inviteMac = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (signature is null || signature.Length != PeerKeypair.SignatureLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A pairing confirmation is {PeerKeypair.SignatureLength} bytes.");
        }

        if (inviteMac is not null && inviteMac.Length != InviteMacLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"An invite proof is {InviteMacLength} bytes.");
        }

        // The branch matters: converting a null byte[] to ReadOnlyMemory<byte>?
        // produces a NON-null empty memory, and an absent proof must stay
        // absent — an empty one would be "present and wrong".
        return inviteMac is { } mac ? new PairConfirm(signature, mac) : new PairConfirm(signature);
    }

    /// <summary>Whether two confirmations are the same bytes.</summary>
    /// <param name="other">The confirmation to compare.</param>
    /// <returns><see langword="true"/> when the signatures match.</returns>
    public bool Equals(PairConfirm? other) =>
        other is not null
        && Signature.Span.SequenceEqual(other.Signature.Span)
        && (InviteMac, other.InviteMac) switch
        {
            (null, null) => true,
            ({ } ours, { } theirs) => ours.Span.SequenceEqual(theirs.Span),
            _ => false,
        };

    /// <inheritdoc/>
    public override int GetHashCode() => Signature.Length;
}

/// <summary>
/// Either side declining, before or after seeing the string
/// (specification peer-protocol 01 §2.2).
/// </summary>
/// <param name="Reason">The closed-set code, shared with 02 §8.</param>
/// <param name="Text">What a human reads. Nothing may parse it.</param>
public sealed record PairRefuse(PeerRefusalReason Reason, string Text) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.PairRefuse;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt32((uint)Reason);
        writer.WriteInt32(2);
        writer.WriteTextString(Text);
    }

    /// <summary>Reads a refusal from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="PeerProtocolException">The body violates 01 §2.2.</exception>
    public static PairRefuse Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

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
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return new PairRefuse(reason, text);
    }
}

/// <summary>Encoding shared by the pairing messages (01 §2.2).</summary>
internal static class PairingCbor
{
    /// <summary>The most bytes a human-chosen label may occupy (00 §2.3).</summary>
    public const int MaximumLabelBytes = PairOffer.MaximumLabelBytes;

    /// <summary>Writes keys 1–3, which the offer and the acceptance share.</summary>
    public static void WriteContribution(CborWriter writer, PairingContribution contribution)
    {
        ThrowHelper.ThrowIfNull(contribution);

        writer.WriteInt32(1);
        writer.WriteByteString(contribution.Identity.PublicKey);
        writer.WriteInt32(2);
        writer.WriteByteString(contribution.EphemeralPublicKey.Span);
        writer.WriteInt32(3);
        writer.WriteByteString(contribution.Nonce.Span);
    }

    /// <summary>Writes the terms map of 01 §4.</summary>
    public static void WriteTerms(CborWriter writer, PeerTerms terms)
    {
        writer.WriteStartMap(3);
        writer.WriteInt32(1);
        writer.WriteUInt64(terms.QuotaBytes);
        writer.WriteInt32(2);
        writer.WriteTextString(terms.ScheduleWindow);
        writer.WriteInt32(3);
        writer.WriteUInt32(terms.RetentionFloorGenerations);
        writer.WriteEndMap();
    }

    /// <summary>Refuses a label that exceeds the limit rather than truncating it.</summary>
    /// <param name="label">The human-chosen label.</param>
    /// <returns>The label, unchanged.</returns>
    /// <exception cref="PeerProtocolException">The label is too long.</exception>
    /// <remarks>
    /// Truncated rather than refused would put a label on the wire that is not
    /// the one the human typed, and a label is the thing an operator uses to
    /// recognise which machine they are approving.
    /// </remarks>
    public static string CheckedLabel(string label)
    {
        if (Encoding.UTF8.GetByteCount(label) > MaximumLabelBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A peer label exceeds {MaximumLabelBytes} bytes.");
        }

        return label;
    }

    /// <summary>Compares two contributions by value rather than by reference.</summary>
    public static bool SameContribution(PairingContribution left, PairingContribution right) =>
        left.Identity == right.Identity
        && left.EphemeralPublicKey.Span.SequenceEqual(right.EphemeralPublicKey.Span)
        && left.Nonce.Span.SequenceEqual(right.Nonce.Span);

    /// <summary>The keys an offer or an acceptance carries.</summary>
    public readonly record struct Fields(
        byte[]? Identity,
        byte[]? Ephemeral,
        byte[]? Nonce,
        string Label,
        ushort Version,
        PeerTerms? Terms,
        byte Role,
        byte[]? InviteId)
    {
        /// <summary>The invite identifier, refused when present at the wrong length.</summary>
        /// <returns>The identifier, or null when the offer carries none.</returns>
        /// <exception cref="PeerProtocolException">The identifier is not the shape 01 §2.7 defines.</exception>
        /// <remarks>
        /// Statements, not a switch expression: a switch whose natural type is
        /// <c>byte[]?</c> converts its null result through the user-defined
        /// conversion, which turns absent into present-and-empty.
        /// </remarks>
        public ReadOnlyMemory<byte>? CheckedInviteId()
        {
            if (InviteId is null)
            {
                return null;
            }

            if (InviteId.Length != PairingInviteCode.InviteIdLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"An invite identifier is {PairingInviteCode.InviteIdLength} bytes.");
            }

            return InviteId;
        }

        /// <summary>The declared role, refused when absent or out of vocabulary.</summary>
        /// <returns>The role.</returns>
        /// <exception cref="PeerProtocolException">The message carries no valid role — a build predating the negotiated role must pair again (ADR-0030 Amendment 2).</exception>
        public PeerRole CheckedRole() => Role is >= 1 and <= 3
            ? (PeerRole)Role
            : throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "The pairing message declares no storage role — the peer runs a build from before roles were negotiated; update it and pair again.");

        /// <summary>Builds the contribution, checking every length first.</summary>
        /// <returns>The contribution.</returns>
        /// <exception cref="PeerProtocolException">A field is missing or the wrong length.</exception>
        public PairingContribution Contribution()
        {
            if (Identity is null || Identity.Length != PeerIdentity.KeyLength
                || Ephemeral is null || Ephemeral.Length != PairingTranscript.ExchangeKeyLength
                || Nonce is null || Nonce.Length != PairingTranscript.NonceLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A pairing message is not the shape 01 §2.2 defines.");
            }

            return new PairingContribution(
                PeerIdentity.FromPublicKey(Identity), Ephemeral, Nonce);
        }
    }

    /// <summary>Reads the shared key space of an offer or an acceptance.</summary>
    public static Fields ReadFields(CborReader reader, bool hasTerms)
    {
        byte[]? identity = null;
        byte[]? ephemeral = null;
        byte[]? nonce = null;
        var label = string.Empty;
        ushort version = 0;
        PeerTerms? terms = null;
        byte role = 0;
        byte[]? inviteId = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    identity = reader.ReadByteString();
                    break;
                case 2:
                    ephemeral = reader.ReadByteString();
                    break;
                case 3:
                    nonce = reader.ReadByteString();
                    break;
                case 4:
                    label = reader.ReadTextString();
                    if (Encoding.UTF8.GetByteCount(label) > MaximumLabelBytes)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed, $"A peer label exceeds {MaximumLabelBytes} bytes.");
                    }

                    break;
                case 5:
                    version = PeerCbor.ReadUInt16(reader);
                    break;
                case 6 when hasTerms:
                    terms = ReadTerms(reader);
                    break;
                case 7:
                    role = (byte)PeerCbor.ReadUInt16(reader);
                    break;
                case 8 when !hasTerms:
                    inviteId = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return new Fields(identity, ephemeral, nonce, label, version, terms, role, inviteId);
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
