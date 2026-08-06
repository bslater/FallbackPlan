using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives per-blob keys — the construction the format's confidentiality
/// rests on (specification 03 §5; NFR-SEC-008):
/// <c>blob_key = HKDF-Expand(data_or_metadata_key[generation],
/// "fbp/blob/v1" ‖ blob_salt ‖ writer_id ‖ u64(blob_counter), 32)</c>.
/// </summary>
/// <remarks>
/// The salt, writer, and counter inputs all live in the blob's cleartext
/// envelope, so a reader re-derives the key from the blob alone. A blob key
/// is never stored (specification 03 §5.3): the caller receives it into its
/// own span, uses it, and zeroes it.
/// </remarks>
public static class BlobKeyDeriver
{
    /// <summary>A blob key is 32 bytes.</summary>
    public const int BlobKeyLength = 32;

    /// <summary>A blob salt is 32 bytes, drawn once per blob from a CSPRNG.</summary>
    public const int BlobSaltLength = 32;

    /// <summary>
    /// Derives the blob key into <paramref name="destination"/>.
    /// </summary>
    /// <param name="classKey">The 32-byte data or metadata key for the blob's recorded generation.</param>
    /// <param name="blobSalt">The blob's 32-byte salt from its cleartext envelope.</param>
    /// <param name="writer">The writer identity from the envelope.</param>
    /// <param name="blobCounter">The blob counter from the envelope.</param>
    /// <param name="destination">Receives exactly 32 key bytes; the caller zeroes it after use.</param>
    /// <exception cref="ArgumentException">A key, salt, or destination length is wrong.</exception>
    public static void Derive(
        ReadOnlySpan<byte> classKey,
        ReadOnlySpan<byte> blobSalt,
        WriterId writer,
        ulong blobCounter,
        Span<byte> destination)
    {
        if (classKey.Length != 32)
        {
            throw new ArgumentException("The class key is exactly 32 bytes.", nameof(classKey));
        }

        if (blobSalt.Length != BlobSaltLength)
        {
            throw new ArgumentException($"The blob salt is exactly {BlobSaltLength} bytes.", nameof(blobSalt));
        }

        if (destination.Length != BlobKeyLength)
        {
            throw new ArgumentException($"The destination is exactly {BlobKeyLength} bytes.", nameof(destination));
        }

        var label = "fbp/blob/v1"u8;
        Span<byte> info = stackalloc byte[label.Length + BlobSaltLength + WriterId.Size + sizeof(ulong)];

        label.CopyTo(info);
        blobSalt.CopyTo(info[label.Length..]);
        writer.CopyTo(info[(label.Length + BlobSaltLength)..]);
        BinaryPrimitives.WriteUInt64BigEndian(info[(label.Length + BlobSaltLength + WriterId.Size)..], blobCounter);

        HKDF.Expand(HashAlgorithmName.SHA256, classKey, destination, info);
    }
}
