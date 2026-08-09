using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// The record AAD (specification 04 §4; NFR-SEC-003):
/// <c>repository_id ‖ u16(format_version) ‖ u8(object_type) ‖ object_id ‖
/// u32(ordinal)</c> — exactly 55 bytes. Binding the repository and ordinal is
/// what makes a record moved between repositories or ordinals fail
/// authentication; the blob identifier is deliberately absent so compaction
/// can relocate records without re-encryption.
/// </summary>
public static class RecordAad
{
    /// <summary>The AAD length: 16 + 2 + 1 + 32 + 4 = 55 bytes.</summary>
    public const int Length = 55;

    /// <summary>Writes the 55-byte AAD into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 55 bytes.</exception>
    public static void Write(
        RepositoryId repositoryId,
        ushort formatVersion,
        ObjectType objectType,
        ObjectId objectId,
        uint ordinal,
        Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(Strings.FormatRecordAad_RecordAADExactlyBytesGot(Length, destination.Length), nameof(destination));
        }

        repositoryId.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], formatVersion);
        destination[18] = (byte)objectType;
        objectId.CopyTo(destination[19..]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[51..], ordinal);
    }
}
