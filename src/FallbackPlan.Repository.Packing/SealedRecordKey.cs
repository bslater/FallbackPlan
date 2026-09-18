using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The associated data that pins a format-3 data record's sealed content
/// key to its record (specification 05 §2.2;
/// [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1): repository id ‖ object id, 48 bytes. The
/// <see cref="SealedContentKey"/> construction with the blob identifier
/// replaced by the object identifier — so a share transplanted onto another
/// object's record, or into another repository, fails to open, while a share
/// copied with its record into another blob opens there, which is the point.
/// </summary>
public static class SealedRecordKey
{
    /// <summary>The associated data length: a 16-byte repository id and a 32-byte object id.</summary>
    public const int AadLength = RepositoryId.Size + ObjectId.Size;

    /// <summary>The sealed share's length: 80 bytes, as <see cref="ContentSealing.SealedLength"/>.</summary>
    public const int SealedLength = ContentSealing.SealedLength;

    /// <summary>Writes the associated data for one record's sealed share.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 48 bytes.</exception>
    public static void WriteAad(RepositoryId repositoryId, ObjectId objectId, Span<byte> destination)
    {
        if (destination.Length != AadLength)
        {
            throw new ArgumentException($"A sealed record key's AAD is exactly {AadLength} bytes.", nameof(destination));
        }

        repositoryId.CopyTo(destination[..RepositoryId.Size]);
        objectId.CopyTo(destination[RepositoryId.Size..]);
    }

    /// <summary>Seals a content key for one record.</summary>
    public static byte[] Seal(
        ReadOnlySpan<byte> sealingPublicKey, ReadOnlySpan<byte> contentKey, RepositoryId repositoryId, ObjectId objectId)
    {
        Span<byte> aad = stackalloc byte[AadLength];
        WriteAad(repositoryId, objectId, aad);
        return ContentSealing.Seal(sealingPublicKey, contentKey, aad);
    }

    /// <summary>Opens one record's sealed content key with the derived scalar.</summary>
    /// <exception cref="SealedContentException">The share does not open.</exception>
    public static byte[] Open(
        ReadOnlySpan<byte> sealingPrivateKey, ReadOnlySpan<byte> sealedBytes, RepositoryId repositoryId, ObjectId objectId)
    {
        ThrowHelper.ThrowIfNull(objectId);
        Span<byte> aad = stackalloc byte[AadLength];
        WriteAad(repositoryId, objectId, aad);
        return ContentSealing.Open(sealingPrivateKey, sealedBytes, aad);
    }
}
