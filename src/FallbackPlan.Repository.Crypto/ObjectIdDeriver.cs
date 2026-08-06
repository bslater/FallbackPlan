using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives object identifiers: the keyed, type-separated form of a content
/// identifier that may leave the trust boundary (specification 02 §3;
/// NFR-SEC-004).
/// </summary>
/// <remarks>
/// <c>object_id = HMAC-SHA256(content_id_key, object_type ‖ content_id)</c>,
/// used in full. The single type byte domain-separates identifiers, so
/// identical plaintext stored as a segment and as a manifest yields unrelated
/// identifiers. The content-ID key is repository-scoped — every writer derives
/// the same identifiers, which is what makes cross-writer deduplication work.
/// </remarks>
public sealed class ObjectIdDeriver : IDisposable
{
    private readonly byte[] _contentIdKey;

    /// <summary>
    /// Creates a deriver over the repository's 32-byte content-ID key.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="contentIdKey"/> is not exactly 32 bytes.</exception>
    public ObjectIdDeriver(ReadOnlySpan<byte> contentIdKey)
    {
        if (contentIdKey.Length != 32)
        {
            throw new ArgumentException("The content-ID key is exactly 32 bytes.", nameof(contentIdKey));
        }

        _contentIdKey = contentIdKey.ToArray();
    }

    /// <summary>
    /// Derives the object identifier for <paramref name="contentId"/> under
    /// <paramref name="objectType"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="objectType"/> is not an assigned object type — including
    /// the reserved store-key domain separator <c>0x07</c>, which must never be
    /// used as an object type (specification 02 §3.1).
    /// </exception>
    public ObjectId Derive(ObjectType objectType, ContentId contentId)
    {
        if (!ObjectTypes.IsValid((byte)objectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectType),
                objectType,
                "Not an assigned object type (specification 02 §3.1).");
        }

        Span<byte> message = stackalloc byte[1 + ContentId.Size];
        message[0] = (byte)objectType;
        contentId.CopyTo(message[1..]);

        Span<byte> mac = stackalloc byte[ObjectId.Size];
        HMACSHA256.HashData(_contentIdKey, message, mac);

        return ObjectId.FromBytes(mac);
    }

    /// <summary>Zeroes the held key material.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_contentIdKey);
}
