using System.Buffers.Binary;
using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The associated data that pins a v2 data blob's sealed content key to its
/// blob (ADR-0042 §2; specification 05's v2 envelope): repository id ‖
/// blob id, 32 bytes. Both the writer's seal and the reader's open build it
/// here, so a sealed share transplanted between blobs — or between
/// repositories — fails to open rather than yielding a key.
/// </summary>
public static class SealedContentKey
{
    /// <summary>The associated data length: two 16-byte identities.</summary>
    public const int AadLength = 32;

    /// <summary>Writes the associated data for one blob's sealed share.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 32 bytes.</exception>
    public static void WriteAad(RepositoryId repositoryId, BlobId blobId, Span<byte> destination)
    {
        if (destination.Length != AadLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatSealedContentKey_AadExactlyBytes(AadLength), nameof(destination));
        }

        repositoryId.CopyTo(destination[..16]);
        blobId.CopyTo(destination[16..]);
    }

    /// <summary>Seals a content key for one blob.</summary>
    public static byte[] Seal(
        ReadOnlySpan<byte> sealingPublicKey, ReadOnlySpan<byte> contentKey, RepositoryId repositoryId, BlobId blobId)
    {
        Span<byte> aad = stackalloc byte[AadLength];
        WriteAad(repositoryId, blobId, aad);
        return ContentSealing.Seal(sealingPublicKey, contentKey, aad);
    }

    /// <summary>Opens one blob's sealed content key with the derived scalar.</summary>
    /// <exception cref="SealedContentException">The share does not open.</exception>
    public static byte[] Open(
        ReadOnlySpan<byte> sealingPrivateKey, ReadOnlySpan<byte> sealedBytes, RepositoryId repositoryId, BlobId blobId)
    {
        Span<byte> aad = stackalloc byte[AadLength];
        WriteAad(repositoryId, blobId, aad);
        return ContentSealing.Open(sealingPrivateKey, sealedBytes, aad);
    }
}
