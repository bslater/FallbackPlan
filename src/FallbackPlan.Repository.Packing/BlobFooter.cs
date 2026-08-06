using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The authenticated recovery footer (specification 05 §3): magic
/// <c>FBPKFOOT</c> · record_count u32 · cbor_length u32 · AEAD ciphertext of
/// the CBOR record table · tag. The footer is the format's last line of
/// defence — with it, every record is locatable and verifiable from the blob
/// and keys alone (FR-MAN-007).
/// </summary>
public static class BlobFooter
{
    /// <summary>The footer framing header length: magic + record_count + cbor_length.</summary>
    public const int HeaderLength = 16;

    /// <summary>The footer magic, <c>"FBPKFOOT"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKFOOT"u8;

    /// <summary>
    /// Encodes the record table as deterministic CBOR: an array of maps with
    /// integer keys 1–8 in ascending ordinal (specification 05 §3.1).
    /// </summary>
    public static byte[] EncodeRecordTable(IReadOnlyList<RecordTableEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var writer = new CanonicalCborWriter();
        writer.WriteStartArray(entries.Count);

        Span<byte> objectId = stackalloc byte[ObjectId.Size];
        foreach (var entry in entries)
        {
            entry.ObjectId.CopyTo(objectId);

            writer.WriteStartMap(8);
            writer.WriteKey(1);
            writer.WriteByteString(objectId);
            writer.WriteKey(2);
            writer.WriteUnsignedInteger(entry.Ordinal);
            writer.WriteKey(3);
            writer.WriteUnsignedInteger(entry.PhysicalOffset);
            writer.WriteKey(4);
            writer.WriteUnsignedInteger(entry.StoredLength);
            writer.WriteKey(5);
            writer.WriteUnsignedInteger(entry.LogicalLength);
            writer.WriteKey(6);
            writer.WriteUnsignedInteger(entry.CompressionProfileValue);
            writer.WriteKey(7);
            writer.WriteUnsignedInteger(entry.EncryptionProfileValue);
            writer.WriteKey(8);
            writer.WriteUnsignedInteger((byte)entry.ObjectType);
            writer.WriteEndMap();
        }

        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <summary>
    /// Decodes and validates a record table against the blob it claims to
    /// describe (specification 05 §3.1): the declared count matches, ordinals
    /// ascend and equal their position, physical offsets strictly increase,
    /// every record falls inside the blob, and every profile resolves. Each
    /// violation is a distinct damage finding.
    /// </summary>
    /// <exception cref="BlobFormatException">The table violates a constraint.</exception>
    /// <exception cref="CborFormatException">The bytes violate the deterministic CBOR profile.</exception>
    public static IReadOnlyList<RecordTableEntry> DecodeRecordTable(
        ReadOnlyMemory<byte> plaintextCbor,
        uint declaredRecordCount,
        long blobLength)
    {
        var reader = new CanonicalCborReader(plaintextCbor);
        var count = reader.ReadStartArray(maxCount: FormatLimits.MaxRecordsPerBlob);

        if (count != declaredRecordCount)
        {
            throw new BlobFormatException(
                $"The record table holds {count} entries; the footer declares {declaredRecordCount} — a damage finding (specification 05 §3.1).");
        }

        var entries = new List<RecordTableEntry>(count);
        var previousEnd = (ulong)BlobEnvelope.Length;

        for (var position = 0; position < count; position++)
        {
            var entryCount = reader.ReadStartMap();
            if (entryCount != 8)
            {
                throw new BlobFormatException($"A record-table entry has 8 fields; entry {position} has {entryCount}.");
            }

            ObjectId? objectId = null;
            uint? ordinal = null;
            ulong? physicalOffset = null;
            uint? storedLength = null;
            ulong? logicalLength = null;
            ushort? compressionValue = null;
            ushort? encryptionValue = null;
            byte? objectTypeValue = null;

            for (var field = 0; field < entryCount; field++)
            {
                switch (reader.ReadKey())
                {
                    case 1:
                        objectId = ObjectId.FromBytes(reader.ReadFixedByteString(ObjectId.Size));
                        break;
                    case 2:
                        ordinal = reader.ReadUInt32();
                        break;
                    case 3:
                        physicalOffset = reader.ReadUnsignedInteger();
                        break;
                    case 4:
                        storedLength = reader.ReadUInt32();
                        break;
                    case 5:
                        logicalLength = reader.ReadUnsignedInteger();
                        break;
                    case 6:
                        compressionValue = reader.ReadUInt16();
                        break;
                    case 7:
                        encryptionValue = reader.ReadUInt16();
                        break;
                    case 8:
                        objectTypeValue = reader.ReadByte();
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }

            reader.ReadEndMap();

            if (objectId is null || ordinal is null || physicalOffset is null || storedLength is null ||
                logicalLength is null || compressionValue is null || encryptionValue is null || objectTypeValue is null)
            {
                throw new BlobFormatException($"Record-table entry {position} is missing a required field (specification 05 §3.1).");
            }

            if (ordinal.Value != position)
            {
                throw new BlobFormatException(
                    $"Record-table ordinals must ascend from zero; entry {position} carries ordinal {ordinal} (specification 05 §3.1).");
            }

            if (physicalOffset.Value < previousEnd)
            {
                throw new BlobFormatException(
                    $"Record-table offsets must strictly increase without overlap; entry {position} at {physicalOffset} overlaps the previous record (specification 05 §3.1).");
            }

            var recordEnd = physicalOffset.Value + Format.Records.RecordHeader.Length + storedLength.Value + (ulong)Crypto.RecordCipher.TagLength;
            if (recordEnd > (ulong)blobLength)
            {
                throw new BlobFormatException(
                    $"Record-table entry {position} extends to byte {recordEnd}, past the {blobLength}-byte blob — a damage finding (specification 05 §3.1).");
            }

            if (!CompressionProfile.TryFromValue(compressionValue.Value, out _) ||
                !EncryptionProfile.TryFromValue(encryptionValue.Value, out _) ||
                !ObjectTypes.TryFromValue(objectTypeValue.Value, out var objectType))
            {
                throw new BlobFormatException(
                    $"Record-table entry {position} carries an unassigned profile or object type; refused, not guessed (specification 00 §3).");
            }

            previousEnd = recordEnd;
            entries.Add(new RecordTableEntry(
                objectId.Value,
                ordinal.Value,
                physicalOffset.Value,
                storedLength.Value,
                logicalLength.Value,
                compressionValue.Value,
                encryptionValue.Value,
                objectType));
        }

        reader.ReadEndArray();
        reader.AssertEndOfDocument();

        return entries;
    }

    /// <summary>Writes the 16-byte footer framing header.</summary>
    public static void WriteHeader(uint recordCount, uint cborLength, Span<byte> destination)
    {
        if (destination.Length != HeaderLength)
        {
            throw new ArgumentException($"The footer header is exactly {HeaderLength} bytes.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], recordCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], cborLength);
    }

    /// <summary>
    /// Parses the footer framing header, enforcing the record-count and
    /// cbor-length limits before anything is allocated (specification 00 §8).
    /// </summary>
    /// <exception cref="BlobFormatException">The header violates the framing rules.</exception>
    public static (uint RecordCount, uint CborLength) ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            throw new BlobFormatException($"A footer header is {HeaderLength} bytes; got {data.Length}.");
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new BlobFormatException("The FBPKFOOT magic is absent — the locator points at something that is not a footer.");
        }

        var recordCount = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        if (recordCount > FormatLimits.MaxRecordsPerBlob)
        {
            throw new BlobFormatException(
                $"The footer declares {recordCount} records; the limit is {FormatLimits.MaxRecordsPerBlob} (specification 00 §8).");
        }

        var cborLength = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        if (cborLength > FormatLimits.MaxMetadataObjectSize)
        {
            throw new BlobFormatException(
                $"The footer declares a {cborLength}-byte record table; the limit is {FormatLimits.MaxMetadataObjectSize} (specification 00 §8) — refused before allocation.");
        }

        return (recordCount, cborLength);
    }
}
