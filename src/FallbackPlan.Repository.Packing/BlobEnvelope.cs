using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The 88-byte cleartext blob envelope (specification 05 §2): magic
/// <c>FBPKBLOB</c> · format_version u16 · blob_class u16 · key_generation
/// u32 · blob_id bytes[16] · blob_salt bytes[32] · blob_counter u64 ·
/// writer_id bytes[16]. It carries only key-derivation selectors — no
/// content, no paths, no record count, no timestamps — which is exactly what
/// lets a reader re-derive the blob key from the blob alone.
/// </summary>
public sealed class BlobEnvelope
{
    /// <summary>The envelope length: 88 bytes.</summary>
    public const int Length = 88;

    private readonly byte[] _blobSalt;

    /// <summary>Creates an envelope.</summary>
    /// <exception cref="ArgumentException">The salt is not exactly 32 bytes or the class undefined.</exception>
    public BlobEnvelope(
        ushort formatVersion,
        BlobClass blobClass,
        KeyGeneration keyGeneration,
        BlobId blobId,
        ReadOnlySpan<byte> blobSalt,
        ulong blobCounter,
        WriterId writerId)
    {
        if (!Enum.IsDefined(blobClass))
        {
            throw new ArgumentException($"Blob class 0x{(ushort)blobClass:x4} is not defined (specification 05 §2).", nameof(blobClass));
        }

        if (blobSalt.Length != 32)
        {
            throw new ArgumentException("The blob salt is exactly 32 bytes (specification 05 §2).", nameof(blobSalt));
        }

        FormatVersion = formatVersion;
        BlobClass = blobClass;
        KeyGeneration = keyGeneration;
        BlobId = blobId;
        _blobSalt = blobSalt.ToArray();
        BlobCounter = blobCounter;
        WriterId = writerId;
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

    /// <summary>Writes the 88 envelope bytes.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 88 bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException($"A blob envelope is exactly {Length} bytes; got {destination.Length}.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], FormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)BlobClass);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], KeyGeneration.Value);
        BlobId.CopyTo(destination[16..]);
        _blobSalt.CopyTo(destination[32..]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[64..], BlobCounter);
        WriterId.CopyTo(destination[72..]);
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
            throw new BlobFormatException($"A blob envelope is {Length} bytes; got {data.Length}.");
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new BlobFormatException("Not a FallbackPlan blob: the FBPKBLOB magic is absent.");
        }

        var classValue = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        if (!Enum.IsDefined((BlobClass)classValue))
        {
            throw new BlobFormatException($"Blob class 0x{classValue:x4} is not defined (specification 05 §2).");
        }

        return new BlobEnvelope(
            BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            (BlobClass)classValue,
            new KeyGeneration(BinaryPrimitives.ReadUInt32BigEndian(data[12..])),
            BlobId.FromBytes(data.Slice(16, BlobId.Size)),
            data.Slice(32, 32),
            BinaryPrimitives.ReadUInt64BigEndian(data[64..]),
            WriterId.FromBytes(data.Slice(72, WriterId.Size)));
    }
}
