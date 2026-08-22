using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing.Resources;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The cleartext blob envelope (specification 05 §2): magic
/// <c>FBPKBLOB</c> · format_version u16 · blob_class u16 · key_generation
/// u32 · blob_id bytes[16] · blob_salt bytes[32] · blob_counter u64 ·
/// writer_id bytes[16] — 88 bytes under format v1 and for every
/// metadata-class blob. A format-v2 <em>data</em> blob (ADR-0042 §2)
/// appends its sealed content key — ephemeral share ‖ ciphertext ‖ tag,
/// 80 bytes — for 168 in all. It carries only key-derivation selectors —
/// no content, no paths, no record count, no timestamps — which is exactly
/// what lets a reader re-derive the structure key from the blob alone,
/// while the content key opens only under the repository's derived scalar.
/// </summary>
public sealed class BlobEnvelope
{
    /// <summary>The v1 envelope length, and every envelope's fixed prefix: 88 bytes.</summary>
    public const int Length = 88;

    /// <summary>The largest envelope: the fixed prefix plus a sealed content key.</summary>
    public const int MaxLength = Length + FallbackPlan.Repository.Crypto.ContentSealing.SealedLength;

    private readonly byte[] _blobSalt;
    private readonly byte[]? _sealedContentKey;

    /// <summary>Creates a v1 or v2-metadata envelope (no sealed content key).</summary>
    /// <exception cref="ArgumentException">The salt is not exactly 32 bytes or the class undefined.</exception>
    public BlobEnvelope(
        ushort formatVersion,
        BlobClass blobClass,
        KeyGeneration keyGeneration,
        BlobId blobId,
        ReadOnlySpan<byte> blobSalt,
        ulong blobCounter,
        WriterId writerId)
        : this(formatVersion, blobClass, keyGeneration, blobId, blobSalt, blobCounter, writerId, sealedContentKey: default)
    {
    }

    /// <summary>Creates an envelope, sealed content key included for a v2 data blob.</summary>
    /// <exception cref="ArgumentException">
    /// The salt or sealed share is the wrong length, the class is undefined,
    /// or the sealed share's presence disagrees with the version and class —
    /// a v2 data blob carries one, everything else carries none.
    /// </exception>
    public BlobEnvelope(
        ushort formatVersion,
        BlobClass blobClass,
        KeyGeneration keyGeneration,
        BlobId blobId,
        ReadOnlySpan<byte> blobSalt,
        ulong blobCounter,
        WriterId writerId,
        ReadOnlySpan<byte> sealedContentKey)
    {
        if (!Enum.IsDefined(blobClass))
        {
            throw new ArgumentException(Strings.FormatBlobEnvelope_BlobClassXNotDefined((ushort)blobClass), nameof(blobClass));
        }

        if (blobSalt.Length != 32)
        {
            throw new ArgumentException(Strings.BlobEnvelope_BlobSaltExactlyBytes, nameof(blobSalt));
        }

        var sealedRequired = formatVersion >= FormatLimits.SealedFormatVersion && blobClass == BlobClass.Data;
        if (sealedRequired != !sealedContentKey.IsEmpty
            || (!sealedContentKey.IsEmpty && sealedContentKey.Length != FallbackPlan.Repository.Crypto.ContentSealing.SealedLength))
        {
            throw new ArgumentException(Strings.BlobEnvelope_SealedShareDisagrees, nameof(sealedContentKey));
        }

        FormatVersion = formatVersion;
        BlobClass = blobClass;
        KeyGeneration = keyGeneration;
        BlobId = blobId;
        _blobSalt = blobSalt.ToArray();
        BlobCounter = blobCounter;
        WriterId = writerId;
        _sealedContentKey = sealedContentKey.IsEmpty ? null : sealedContentKey.ToArray();
    }

    /// <summary>The envelope magic, <c>"FBPKBLOB"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKBLOB"u8;

    /// <summary>The blob's format version.</summary>
    public ushort FormatVersion { get; }

    /// <summary>Selects the data or metadata key family (specification 05 §2).</summary>
    public BlobClass BlobClass { get; }

    /// <summary>The key generation the blob key derives under.</summary>
    public KeyGeneration KeyGeneration { get; }

    /// <summary>The writer-allocated blob identifier.</summary>
    public BlobId BlobId { get; }

    /// <summary>The per-blob CSPRNG salt — a blob-key derivation input.</summary>
    public ReadOnlySpan<byte> BlobSalt => _blobSalt;

    /// <summary>The writer's blob counter — a blob-key derivation input.</summary>
    public ulong BlobCounter { get; }

    /// <summary>The writer identity — a blob-key derivation input, carried explicitly (specification 05 §2).</summary>
    public WriterId WriterId { get; }

    /// <summary>
    /// The sealed content key of a v2 data blob — ephemeral share ‖
    /// ciphertext ‖ tag — or empty for every other envelope. Opens only
    /// under the repository's derived scalar (ADR-0042 §2).
    /// </summary>
    public ReadOnlySpan<byte> SealedContentKey => _sealedContentKey;

    /// <summary>This envelope's serialised length: 88, or 168 with a sealed content key.</summary>
    public int EnvelopeLength => Length + (_sealedContentKey?.Length ?? 0);

    /// <summary>Writes the envelope bytes.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly <see cref="EnvelopeLength"/> bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != EnvelopeLength)
        {
            throw new ArgumentException(Strings.FormatBlobEnvelope_BlobEnvelopeExactlyBytesGot(EnvelopeLength, destination.Length), nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], FormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)BlobClass);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], KeyGeneration.Value);
        BlobId.CopyTo(destination[16..]);
        _blobSalt.CopyTo(destination[32..]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[64..], BlobCounter);
        WriterId.CopyTo(destination[72..]);
        _sealedContentKey?.CopyTo(destination[Length..]);
    }

    /// <summary>
    /// Parses an envelope from untrusted bytes. Absent magic is reported as
    /// "not a FallbackPlan blob" rather than as a field error — the object is
    /// something else entirely (the specification 01 §3.1 posture).
    /// </summary>
    /// <exception cref="BlobFormatException">The bytes violate the envelope rules.</exception>
    public static BlobEnvelope Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
        {
            throw new BlobFormatException(Strings.FormatBlobEnvelope_BlobEnvelopeBytesGot(Length, data.Length));
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new BlobFormatException(Strings.BlobEnvelope_NotFallbackPlanBlobFBPKBLOBMagic);
        }

        var classValue = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        if (!Enum.IsDefined((BlobClass)classValue))
        {
            throw new BlobFormatException(Strings.FormatBlobEnvelope_BlobClassXNotDefined(classValue));
        }

        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var blobClass = (BlobClass)classValue;

        // A v2 data blob's envelope carries its sealed content key; a blob
        // claiming that shape without the bytes is damaged, not shorter.
        var sealedContentKey = ReadOnlySpan<byte>.Empty;
        if (formatVersion >= FormatLimits.SealedFormatVersion && blobClass == BlobClass.Data)
        {
            if (data.Length < MaxLength)
            {
                throw new BlobFormatException(Strings.FormatBlobEnvelope_SealedEnvelopeBytesGot(MaxLength, data.Length));
            }

            sealedContentKey = data.Slice(Length, FallbackPlan.Repository.Crypto.ContentSealing.SealedLength);
        }

        return new BlobEnvelope(
            formatVersion,
            blobClass,
            new KeyGeneration(BinaryPrimitives.ReadUInt32BigEndian(data[12..])),
            BlobId.FromBytes(data.Slice(16, BlobId.Size)),
            data.Slice(32, 32),
            BinaryPrimitives.ReadUInt64BigEndian(data[64..]),
            WriterId.FromBytes(data.Slice(72, WriterId.Size)),
            sealedContentKey);
    }
}
