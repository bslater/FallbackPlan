using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives store blob keys: the mandatory keyed rendering of a blob identifier
/// before it appears in any store key (specification 02 §4.3). A blob
/// identifier's leading bytes may be writer identity, so the raw identifier
/// never reaches the store namespace.
/// </summary>
/// <remarks>
/// <c>store_blob_key = HMAC-SHA256(key_id_key, 0x07 ‖ blob_id)[0..16]</c>.
/// The derivation hides writer identity from store <em>keys</em> only — the
/// full writer identifier remains visible in each blob's cleartext envelope,
/// a recorded residual of threat T-11.
/// </remarks>
public sealed class StoreBlobKeyDeriver : IDisposable
{
    /// <summary>
    /// The domain separator reserved for this derivation. It is deliberately a
    /// value that must never be assigned as a record object type
    /// (specification 02 §3.1), so a store-key MAC can never collide with an
    /// object-identifier MAC under the same key family.
    /// </summary>
    private const byte StoreKeyDomainSeparator = 0x07;

    private readonly byte[] _keyIdKey;

    /// <summary>
    /// Creates a deriver over the repository's 32-byte key-ID key.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="keyIdKey"/> is not exactly 32 bytes.</exception>
    public StoreBlobKeyDeriver(ReadOnlySpan<byte> keyIdKey)
    {
        if (keyIdKey.Length != 32)
        {
            throw new ArgumentException(Strings.StoreBlobKeyDeriver_KeyIDKeyExactlyBytes, nameof(keyIdKey));
        }

        _keyIdKey = keyIdKey.ToArray();
    }

    /// <summary>Derives the store blob key for <paramref name="blobId"/>.</summary>
    public StoreBlobKey Derive(BlobId blobId)
    {
        Span<byte> message = stackalloc byte[1 + BlobId.Size];
        message[0] = StoreKeyDomainSeparator;
        blobId.CopyTo(message[1..]);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_keyIdKey, message, mac);

        return StoreBlobKey.FromBytes(mac[..StoreBlobKey.Size]);
    }

    /// <summary>Zeroes the held key material.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_keyIdKey);
}
