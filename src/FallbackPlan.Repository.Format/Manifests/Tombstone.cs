using Bodu;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>Why an object was marked for deletion (specification 11 §3, closed set).</summary>
public enum TombstoneReason : ushort
{
    /// <summary>No protected snapshot reaches it.</summary>
    Unreferenced = 1,

    /// <summary>An index delta a checkpoint retired.</summary>
    RetiredDelta = 2,

    /// <summary>Its records were rewritten into replacement blobs.</summary>
    Compacted = 3,

    /// <summary>A newer object superseded it.</summary>
    Superseded = 4,
}

/// <summary>
/// A tombstone: the record that an object has been marked for deletion and
/// when it becomes eligible (specification 11 §3). It authorises — which is
/// why it is the one lifecycle object that is signed — and its grace is
/// counted in generations, never wall clock: there is no trusted time source,
/// and a wall-clock grace is a grace an adversary ends early by moving a clock.
/// </summary>
/// <param name="ObjectTypeCode">
/// The 02 §3.1 type of the doomed object — or <c>0x07</c>, the reserved blob
/// domain, for a blob, which has no object type of its own.
/// </param>
/// <param name="ObjectId">32 bytes, or 16 for a blob — width validated against the type.</param>
/// <param name="Reason">Why, from the closed vocabulary.</param>
/// <param name="WriterId">Who marked it.</param>
/// <param name="TombstonedAtUnixMilliseconds">Informational only; MUST NOT decide eligibility.</param>
/// <param name="EligibleGeneration">The index generation at or after which the delete may proceed.</param>
public sealed record Tombstone(
    byte ObjectTypeCode,
    ReadOnlyMemory<byte> ObjectId,
    TombstoneReason Reason,
    ReadOnlyMemory<byte> WriterId,
    ulong TombstonedAtUnixMilliseconds,
    ulong EligibleGeneration)
{
    /// <summary>The reserved code a blob tombstone carries — the 02 §4.3 blob domain, assignable to no record.</summary>
    public const byte BlobTypeCode = 0x07;

    /// <summary>The identifier width this tombstone's type requires (11 §3.1).</summary>
    public int RequiredIdWidth => ObjectTypeCode == BlobTypeCode ? 16 : 32;
}

/// <summary>A decoded tombstone with the bytes its signature covers.</summary>
/// <param name="Value">The tombstone.</param>
/// <param name="SignedBytes">The canonical encoding of keys 1–7 — what the signature is verified over.</param>
/// <param name="Signature">The Ed25519 signature from key 8.</param>
public sealed record DecodedTombstone(Tombstone Value, ReadOnlyMemory<byte> SignedBytes, ReadOnlyMemory<byte> Signature);

/// <summary>
/// The wire codec for specification 11 §3: canonical CBOR, keys 1–7 signed,
/// the signature at key 8. A tombstone that fails its signature is a
/// <b>security finding</b>, never mere damage — an unsigned or forged
/// tombstone is an attempt to have someone else delete data — and that
/// verdict belongs to the caller holding the signer.
/// </summary>
public static class TombstoneCodec
{
    private const ushort SchemaVersion = 1;

    /// <summary>Encodes keys 1–7 — the bytes a signature covers.</summary>
    /// <param name="tombstone">The tombstone to encode.</param>
    /// <returns>The canonical signed prefix.</returns>
    public static byte[] EncodeForSigning(Tombstone tombstone)
    {
        ThrowHelper.ThrowIfNull(tombstone);
        Validate(tombstone);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, tombstone, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–7 plus the 64-byte signature at key 8.</summary>
    /// <param name="tombstone">The tombstone to encode.</param>
    /// <param name="signature">The Ed25519 signature over <see cref="EncodeForSigning"/>'s bytes.</param>
    /// <returns>The stored encoding.</returns>
    public static byte[] Encode(Tombstone tombstone, ReadOnlySpan<byte> signature)
    {
        ThrowHelper.ThrowIfNull(tombstone);
        Validate(tombstone);

        if (signature.Length != 64)
        {
            throw new ManifestValidationException(Strings.TombstoneCodec_SignatureExactlyBytes);
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, tombstone, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>Decodes a stored tombstone, rebuilding the signed prefix for the caller to verify.</summary>
    /// <param name="data">The stored bytes.</param>
    /// <returns>The decoded tombstone.</returns>
    /// <exception cref="ManifestValidationException">The bytes violate 11 §3.</exception>
    public static DecodedTombstone Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                Strings.FormatTombstoneCodec_NotCanonicalCbor(exception.Message), exception);
        }
    }

    private static DecodedTombstone DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ushort? version = null;
        byte? objectType = null;
        byte[]? objectId = null;
        ushort? reason = null;
        byte[]? writerId = null;
        ulong tombstonedAt = 0;
        ulong? eligibleGeneration = null;
        byte[]? signature = null;

        for (var index = 0; index < count; index++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    version = reader.ReadUInt16();
                    break;
                case 2:
                    objectType = reader.ReadByte();
                    break;
                case 3:
                    objectId = reader.ReadByteString(32);
                    break;
                case 4:
                    reason = reader.ReadUInt16();
                    break;
                case 5:
                    writerId = reader.ReadByteString(16);
                    break;
                case 6:
                    tombstonedAt = reader.ReadUnsignedInteger();
                    break;
                case 7:
                    eligibleGeneration = reader.ReadUnsignedInteger();
                    break;
                case 8:
                    signature = reader.ReadByteString(64);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();

        if (version is null || objectType is null || objectId is null
            || reason is null || writerId is null || eligibleGeneration is null || signature is null)
        {
            throw new ManifestValidationException(Strings.FormatTombstoneCodec_RequiredKeyMissing(
                version is null ? 1 : objectType is null ? 2 : objectId is null ? 3
                : reason is null ? 4 : writerId is null ? 5 : eligibleGeneration is null ? 7 : 8));
        }

        if (version != SchemaVersion)
        {
            throw new ManifestValidationException(Strings.FormatTombstoneCodec_SchemaVersionUnsupported(version.Value));
        }

        var tombstone = new Tombstone(
            objectType.Value, objectId, (TombstoneReason)reason.Value, writerId,
            tombstonedAt, eligibleGeneration.Value);
        Validate(tombstone);

        if (signature.Length != 64)
        {
            throw new ManifestValidationException(Strings.TombstoneCodec_SignatureExactlyBytes);
        }

        return new DecodedTombstone(tombstone, EncodeForSigning(tombstone), signature);
    }

    private static void Validate(Tombstone tombstone)
    {
        if (!Enum.IsDefined(tombstone.Reason))
        {
            throw new ManifestValidationException(
                Strings.FormatTombstoneCodec_ReasonOutsideVocabulary((ushort)tombstone.Reason));
        }

        // The width rule (11 §3.1): a mismatch is refused, because a 16-byte
        // id under a 32-byte type is either damage or an attempt to alias.
        if (tombstone.ObjectId.Length != tombstone.RequiredIdWidth)
        {
            throw new ManifestValidationException(Strings.FormatTombstoneCodec_ObjectIdWidthMismatch(
                tombstone.ObjectTypeCode, tombstone.ObjectId.Length, tombstone.RequiredIdWidth));
        }

        if (tombstone.WriterId.Length != 16)
        {
            throw new ManifestValidationException(Strings.FormatTombstoneCodec_RequiredKeyMissing(5));
        }
    }

    private static void WriteBody(CanonicalCborWriter writer, Tombstone tombstone, byte[]? signature)
    {
        writer.WriteStartMap(7 + (signature is not null ? 1 : 0));
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(SchemaVersion);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(tombstone.ObjectTypeCode);
        writer.WriteKey(3);
        writer.WriteByteString(tombstone.ObjectId.Span);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger((ushort)tombstone.Reason);
        writer.WriteKey(5);
        writer.WriteByteString(tombstone.WriterId.Span);
        writer.WriteKey(6);
        writer.WriteUnsignedInteger(tombstone.TombstonedAtUnixMilliseconds);
        writer.WriteKey(7);
        writer.WriteUnsignedInteger(tombstone.EligibleGeneration);

        if (signature is not null)
        {
            writer.WriteKey(8);
            writer.WriteByteString(signature);
        }

        writer.WriteEndMap();
    }
}
