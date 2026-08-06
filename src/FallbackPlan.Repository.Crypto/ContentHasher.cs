using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Computes content identifiers under the <c>sha-256-v1</c> profile: SHA-256
/// over a segment's plaintext, before compression and before encryption, used
/// in full with no truncation (specification 02 §2; FR-ARCH-003).
/// </summary>
/// <remarks>
/// The incremental surface exists so segment hashing can be pipelined with
/// reading — a segment is never required to be resident in one buffer. The
/// resulting <see cref="ContentId"/> never leaves the trust boundary
/// (NFR-SEC-004); its rendering is redacted at the type level.
/// </remarks>
public sealed class ContentHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Appends a slice of segment plaintext.</summary>
    public void Append(ReadOnlySpan<byte> plaintext) => _hash.AppendData(plaintext);

    /// <summary>
    /// Completes the hash, returns the content identifier, and resets for the
    /// next segment.
    /// </summary>
    public ContentId FinishAndReset()
    {
        Span<byte> digest = stackalloc byte[ContentId.Size];
        _hash.GetHashAndReset(digest);

        return ContentId.FromBytes(digest);
    }

    /// <summary>Hashes a fully resident plaintext in one call.</summary>
    public static ContentId Hash(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> digest = stackalloc byte[ContentId.Size];
        SHA256.HashData(plaintext, digest);

        return ContentId.FromBytes(digest);
    }

    /// <inheritdoc />
    public void Dispose() => _hash.Dispose();
}
