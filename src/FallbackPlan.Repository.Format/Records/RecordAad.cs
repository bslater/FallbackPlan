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
/// authentication.
/// </summary>
/// <remarks>
/// The blob identifier is absent because binding it would be redundant, not
/// because a record can be relocated: the record's key already derives from
/// its blob's salt, writer and counter, so it cannot be opened under any
/// other blob's context at all (04 §4 as ADR-0025 §3 rewrote it;
/// <c>RecordCipherTests.RecordCipher_MovedToADifferentBlob_FailsBecauseTheKeyDoesNotTravel</c>).
/// This comment used to give the opposite reason — that the blob id is out so
/// compaction can relocate records without re-encryption — which was the
/// contradiction ADR-0025 exists to resolve. Format v3 changes the underlying
/// property and drops the ordinal from this layout
/// (<see cref="WriteRelocatable"/>); v1 and v2 records keep it.
/// </remarks>
public static class RecordAad
{
    /// <summary>The AAD length: 16 + 2 + 1 + 32 + 4 = 55 bytes.</summary>
    public const int Length = 55;

    /// <summary>The format-3 AAD length: 16 + 2 + 1 + 32 = 51 bytes — no ordinal (04 §4).</summary>
    public const int RelocatableLength = 51;

    /// <summary>
    /// Writes the 51-byte format-3 AAD: <c>repository_id ‖ u16(format_version)
    /// ‖ u8(object_type) ‖ object_id</c>. The record's position is framing
    /// only ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
    /// §4): in-blob reordering is caught by the footer's sealed record table,
    /// and a record moved to another blob keeps its key, its nonce and this
    /// AAD, which is what makes the move a copy.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is not exactly 51 bytes, or the version has positional records.</exception>
    public static void WriteRelocatable(
        RepositoryId repositoryId,
        ushort formatVersion,
        ObjectType objectType,
        ObjectId objectId,
        Span<byte> destination)
    {
        if (destination.Length != RelocatableLength)
        {
            throw new ArgumentException(
                Strings.FormatRecordAad_RecordAADExactlyBytesGot(RelocatableLength, destination.Length), nameof(destination));
        }

        if (!FormatVersions.HasRelocatableRecords(formatVersion))
        {
            throw new ArgumentException(
                $"Format {formatVersion} records bind their ordinal; only format {FormatVersions.RelocatableRecords} "
                + "and later omit it (specification 04 §4).",
                nameof(formatVersion));
        }

        repositoryId.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], formatVersion);
        destination[18] = (byte)objectType;
        objectId.CopyTo(destination[19..]);
    }

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
