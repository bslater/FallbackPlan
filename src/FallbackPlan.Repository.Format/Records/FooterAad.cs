using System.Buffers.Binary;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// The recovery footer's AAD (specification 05 §3):
/// <c>repository_id ‖ u16(format_version) ‖ blob_id ‖ u32(record_count)</c> —
/// exactly 38 bytes. Unlike a record's AAD it binds the blob identifier: a
/// footer describes one specific blob and must not authenticate against any
/// other.
/// </summary>
public static class FooterAad
{
    /// <summary>The AAD length: 16 + 2 + 16 + 4 = 38 bytes.</summary>
    public const int Length = 38;

    /// <summary>Writes the 38-byte AAD into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 38 bytes.</exception>
    public static void Write(
        RepositoryId repositoryId,
        ushort formatVersion,
        BlobId blobId,
        uint recordCount,
        Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(Strings.FormatFooterAad_FooterAADExactlyBytesGot(Length, destination.Length), nameof(destination));
        }

        repositoryId.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], formatVersion);
        blobId.CopyTo(destination[18..]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[34..], recordCount);
    }
}
