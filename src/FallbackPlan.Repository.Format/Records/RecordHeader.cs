using Bodu;
using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// The 54-byte cleartext record header (specification 04 §2; FR-ARCH-009):
/// marker <c>0x52</c> · object_type u8 · compression_profile u16 ·
/// encryption_profile u16 · ordinal u32 · logical_length u64 · stored_length
/// u32 · object_id bytes[32]. Cleartext but not unauthenticated — its
/// load-bearing fields are bound into the AAD.
/// </summary>
/// <remarks>
/// Construction enforces every field rule of 04 §2.1, so an invalid header is
/// unrepresentable on the write side; <see cref="Parse"/> enforces the same
/// rules against untrusted bytes, checking <c>stored_length</c> against the
/// 64 MiB limit before any caller could allocate for it. A
/// <c>logical_length</c> of zero is a damage finding: zero-length files
/// produce no records at all (04 §2.1, 09 §2.1).
/// </remarks>
public readonly struct RecordHeader
{
    /// <summary>The header length: 54 bytes.</summary>
    public const int Length = 54;

    /// <summary>The record marker, <c>0x52</c> — a cheap filter, never proof (04 §2.1).</summary>
    public const byte Marker = 0x52;

    /// <summary>The largest permitted ordinal: 65 535 (04 §2.1).</summary>
    public const uint MaxOrdinal = 65_535;

    /// <summary>Creates a header, enforcing every field rule of 04 §2.1.</summary>
    /// <exception cref="ArgumentException">A field violates the framing rules.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A length or ordinal is out of range.</exception>
    public RecordHeader(
        ObjectType objectType,
        CompressionProfile compressionProfile,
        EncryptionProfile encryptionProfile,
        uint ordinal,
        ulong logicalLength,
        uint storedLength,
        ObjectId objectId)
    {
        ThrowHelper.ThrowIfNull(compressionProfile);
        ThrowHelper.ThrowIfNull(encryptionProfile);

        if (!ObjectTypes.IsValid((byte)objectType))
        {
            throw new ArgumentException(Strings.RecordHeader_NotAssignedObjectType, nameof(objectType));
        }

        ThrowHelper.ThrowIfGreaterThan(ordinal, MaxOrdinal);
        ThrowHelper.ThrowIfEqual(logicalLength, 0UL);
        ThrowHelper.ThrowIfGreaterThan(storedLength, (uint)FormatLimits.MaxRecordStoredLength);

        if (compressionProfile == CompressionProfile.None && storedLength != logicalLength)
        {
            throw new ArgumentException(Strings.RecordHeader_WithCompressionProfileNoneStored,
                nameof(storedLength));
        }

        ObjectType = objectType;
        CompressionProfile = compressionProfile;
        EncryptionProfile = encryptionProfile;
        Ordinal = ordinal;
        LogicalLength = logicalLength;
        StoredLength = storedLength;
        ObjectId = objectId;
    }

    /// <summary>The record's object type.</summary>
    public ObjectType ObjectType { get; }

    /// <summary>The compression profile, recorded per record (specification 10 §2).</summary>
    public CompressionProfile CompressionProfile { get; }

    /// <summary>The encryption profile, recorded per record (specification 03 §6).</summary>
    public EncryptionProfile EncryptionProfile { get; }

    /// <summary>The record's zero-based position in its blob.</summary>
    public uint Ordinal { get; }

    /// <summary>The plaintext length before compression; always at least 1.</summary>
    public ulong LogicalLength { get; }

    /// <summary>The ciphertext length; at most 64 MiB.</summary>
    public uint StoredLength { get; }

    /// <summary>The record's object identifier.</summary>
    public ObjectId ObjectId { get; }

    /// <summary>Writes the 54 header bytes.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 54 bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(Strings.FormatRecordHeader_RecordHeaderExactlyBytesGot(Length, destination.Length), nameof(destination));
        }

        destination[0] = Marker;
        destination[1] = (byte)ObjectType;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], CompressionProfile.Value);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], EncryptionProfile.Value);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], Ordinal);
        BinaryPrimitives.WriteUInt64BigEndian(destination[10..], LogicalLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[18..], StoredLength);
        ObjectId.CopyTo(destination[22..]);
    }

    /// <summary>
    /// Parses a header from untrusted bytes, refusing every violation of
    /// 04 §2.1 with a message naming it.
    /// </summary>
    /// <exception cref="RecordFormatException">The bytes violate the framing rules.</exception>
    public static RecordHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_RecordHeaderBytesGot(Length, data.Length));
        }

        if (data[0] != Marker)
        {
            throw new RecordFormatException(Strings.RecordHeader_RecordMarkerXAbsent);
        }

        if (!ObjectTypes.TryFromValue(data[1], out var objectType))
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_ObjectTypeXNotAssigned(data[1]));
        }

        var compressionValue = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (!CompressionProfile.TryFromValue(compressionValue, out var compressionProfile))
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_CompressionProfileXUnknownRefused(compressionValue));
        }

        var encryptionValue = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (!EncryptionProfile.TryFromValue(encryptionValue, out var encryptionProfile))
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_EncryptionProfileXUnknownRefused(encryptionValue));
        }

        var ordinal = BinaryPrimitives.ReadUInt32BigEndian(data[6..]);
        if (ordinal > MaxOrdinal)
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_OrdinalExceeds(ordinal, MaxOrdinal));
        }

        var logicalLength = BinaryPrimitives.ReadUInt64BigEndian(data[10..]);
        if (logicalLength == 0)
        {
            throw new RecordFormatException(Strings.RecordHeader_LogicalLengthZero);
        }

        var storedLength = BinaryPrimitives.ReadUInt32BigEndian(data[18..]);
        if (storedLength > FormatLimits.MaxRecordStoredLength)
        {
            throw new RecordFormatException(Strings.FormatRecordHeader_StoredLengthExceedsMiBRecord(storedLength));
        }

        if (compressionProfile == CompressionProfile.None && storedLength != logicalLength)
        {
            throw new RecordFormatException(Strings.RecordHeader_WithCompressionProfileNoneStored2);
        }

        return new RecordHeader(
            objectType,
            compressionProfile!,
            encryptionProfile!,
            ordinal,
            logicalLength,
            storedLength,
            ObjectId.FromBytes(data.Slice(22, ObjectId.Size)));
    }
}
