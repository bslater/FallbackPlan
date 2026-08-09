using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>
/// The derived keys a session needs, bundled so the master key itself is
/// handled exactly once (specification 03 §4): the repository-scoped
/// content-ID and key-ID keys, and data or metadata class keys by
/// generation. Dispose zeroes everything (specification 03 §8).
/// </summary>
public sealed class RepositoryKeySet : IDisposable
{
    private readonly KeyHierarchy _hierarchy;
    private readonly byte[] _contentIdKey;
    private readonly byte[] _keyIdKey;

    private RepositoryKeySet(KeyHierarchy hierarchy, byte[] contentIdKey, byte[] keyIdKey)
    {
        _hierarchy = hierarchy;
        _contentIdKey = contentIdKey;
        _keyIdKey = keyIdKey;
    }

    /// <summary>Derives the set from a 32-byte master key. The bytes are copied.</summary>
    public static RepositoryKeySet FromMasterKey(ReadOnlySpan<byte> masterKey)
    {
        var hierarchy = new KeyHierarchy(masterKey);

        return new RepositoryKeySet(hierarchy, hierarchy.DeriveContentIdKey(), hierarchy.DeriveKeyIdKey());
    }

    /// <summary>The repository-scoped content-ID key.</summary>
    public ReadOnlySpan<byte> ContentIdKey => _contentIdKey;

    /// <summary>The repository-scoped key-ID key.</summary>
    public ReadOnlySpan<byte> KeyIdKey => _keyIdKey;

    /// <summary>
    /// Derives the class key — data or metadata — for
    /// <paramref name="generation"/>. The caller owns and zeroes the result.
    /// </summary>
    public byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) => blobClass switch
    {
        BlobClass.Data => _hierarchy.DeriveDataKey(generation),
        BlobClass.Metadata => _hierarchy.DeriveMetadataKey(generation),
        _ => throw new ArgumentException(Strings.FormatRepositoryKeySet_BlobClassXNotDefined((ushort)blobClass), nameof(blobClass)),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _hierarchy.Dispose();
        CryptographicOperations.ZeroMemory(_contentIdKey);
        CryptographicOperations.ZeroMemory(_keyIdKey);
    }
}
