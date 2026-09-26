using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>
/// The derived keys a session needs, bundled so the credential is handled
/// exactly once (specification 03 §9): the repository-scoped content-ID and
/// key-ID keys, and the metadata class key by generation. There is no data
/// class key — content is sealed to the repository's public key, and a code
/// path asking for one is a bug, not a missing capability. Dispose zeroes
/// everything (specification 03 §8).
/// </summary>
public sealed class RepositoryKeySet : IDisposable
{
    private readonly RepositoryWriteCredential _credential;
    private readonly byte[] _contentIdKey;
    private readonly byte[] _keyIdKey;

    private RepositoryKeySet(RepositoryWriteCredential credential, byte[] contentIdKey, byte[] keyIdKey)
    {
        _credential = credential;
        _contentIdKey = contentIdKey;
        _keyIdKey = keyIdKey;
    }

    /// <summary>
    /// Derives the set from the repository's write credential (ADR-0042).
    /// The credential is cloned.
    /// </summary>
    public static RepositoryKeySet FromWriteCredential(RepositoryWriteCredential credential)
    {
        var owned = credential.Clone();

        return new RepositoryKeySet(owned, owned.ContentIdKey.ToArray(), owned.KeyIdKey.ToArray());
    }

    /// <summary>The repository's sealing public key — what content seals to.</summary>
    public ReadOnlySpan<byte> SealingPublicKey => _credential.SealingPublicKey;

    /// <summary>The repository-scoped content-ID key.</summary>
    public ReadOnlySpan<byte> ContentIdKey => _contentIdKey;

    /// <summary>The repository-scoped key-ID key.</summary>
    public ReadOnlySpan<byte> KeyIdKey => _keyIdKey;

    /// <summary>
    /// Derives the class key for <paramref name="generation"/>. Only the
    /// metadata class has one; the caller owns and zeroes the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">The data class was asked for: a repository holds no data key (specification 03 §9.2).</exception>
    public byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) => blobClass switch
    {
        BlobClass.Metadata => _credential.DeriveMetadataKey(generation),
        BlobClass.Data => throw new InvalidOperationException(Strings.RepositoryKeySet_NoDataClassKey),
        _ => throw new ArgumentException(Strings.FormatRepositoryKeySet_BlobClassXNotDefined((ushort)blobClass), nameof(blobClass)),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _credential.Dispose();
        CryptographicOperations.ZeroMemory(_contentIdKey);
        CryptographicOperations.ZeroMemory(_keyIdKey);
    }
}
