using Bodu;
using System.Formats.Cbor;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>
/// What a destination does with one offered replacement, and then with the
/// bytes it asked for (specification peer-protocol 08 §3.2). Every verdict
/// but <see cref="Send"/> ends the exchange for that key.
/// </summary>
public enum RepairVerdictCode : ushort
{
    /// <summary>Its copy fails the locator check and is the offered length: send the replacement.</summary>
    Send = 1,

    /// <summary>The replacement passed and stands in place of the damaged copy.</summary>
    Replaced = 2,

    /// <summary>Its copy passes the locator check, so it keeps it.</summary>
    Intact = 3,

    /// <summary>It holds nothing under the key; a repair never creates an object.</summary>
    NotHeld = 4,

    /// <summary>Its copy is not the offered length, so nothing on it names the blob a replacement must be.</summary>
    LengthDiffers = 5,

    /// <summary>The replacement failed the locator check, or stated a locator other than its copy's.</summary>
    Unmatched = 6,
}

/// <summary>
/// An owner's offer of a replacement for one blob its sweep found damaged at
/// the destination (specification peer-protocol 08 §3.1). The first offer of
/// a session opens the exchange, and every offer in it names the same
/// repository.
/// </summary>
/// <param name="RepositoryId">The replica's repository identity (16 bytes).</param>
/// <param name="Key">The blob's store key.</param>
/// <param name="Length">The length of the blob the source would send.</param>
public sealed record RepairOffer(ReadOnlyMemory<byte> RepositoryId, string Key, ulong Length) : IPeerMessage
{
    /// <summary>The repository identity's length in bytes.</summary>
    public const int RepositoryIdLength = 16;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RepairOffer;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteTextString(Key);
        writer.WriteInt32(3);
        writer.WriteUInt64(Length);
    }

    /// <summary>Reads an offer from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 08 §3.1.</exception>
    public static RepairOffer Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        string? objectKey = null;
        ulong? length = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    repositoryId = reader.ReadByteString();
                    break;
                case 2:
                    objectKey = reader.ReadTextString();
                    break;
                case 3:
                    length = reader.ReadUInt64();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is null || repositoryId.Length != RepositoryIdLength
            || objectKey is null || objectKey.Length == 0
            || Encoding.UTF8.GetByteCount(objectKey) > ReplicationInventory.MaximumKeyBytes
            || length is null)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A repair offer is not the shape 08 §3.1 defines.");
        }

        return new RepairOffer(repositoryId, objectKey, length.Value);
    }

    /// <inheritdoc/>
    public bool Equals(RepairOffer? other) =>
        other is not null
        && Length == other.Length
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key, Length, RepositoryId.Length);
}

/// <summary>
/// The destination's answer to an offer, and again to the bytes it asked for
/// (specification peer-protocol 08 §3.2): reached from its own copy and the
/// bytes it received, and from nothing the source said of them.
/// </summary>
/// <param name="Key">The blob's store key, as offered.</param>
/// <param name="Verdict">What the destination did, or will do.</param>
public sealed record RepairVerdict(string Key, RepairVerdictCode Verdict) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RepairVerdict;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteTextString(Key);
        writer.WriteInt32(2);
        writer.WriteUInt32((uint)Verdict);
    }

    /// <summary>Reads a verdict from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 08 §3.2.</exception>
    public static RepairVerdict Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        string? objectKey = null;
        uint? verdict = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    objectKey = reader.ReadTextString();
                    break;
                case 2:
                    verdict = reader.ReadUInt32();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (objectKey is null || objectKey.Length == 0
            || Encoding.UTF8.GetByteCount(objectKey) > ReplicationInventory.MaximumKeyBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A repair verdict is not the shape 08 §3.2 defines.");
        }

        // A verdict decides whether a blob crosses. One this build does not
        // define is not one it may read as any of those it does.
        if (verdict is not { } code || code > ushort.MaxValue || !Enum.IsDefined((RepairVerdictCode)code))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"A repair verdict carries a code 08 §3.2 does not define ({verdict}).");
        }

        return new RepairVerdict(objectKey, (RepairVerdictCode)code);
    }
}
