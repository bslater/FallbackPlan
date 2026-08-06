using System.Security.Cryptography;

namespace FallbackPlan.Repository.Format.Keys;

/// <summary>
/// The decrypted key bundle (specification 03 §3.1): the master key and the
/// current generations. Exists only in memory, only between unwrap and use;
/// disposal zeroes the master key.
/// </summary>
public sealed class KeyBundle : IDisposable
{
    private readonly byte[] _masterKey;

    /// <summary>Creates a bundle. The master-key bytes are copied.</summary>
    /// <exception cref="ArgumentException"><paramref name="masterKey"/> is not exactly 32 bytes.</exception>
    public KeyBundle(ReadOnlySpan<byte> masterKey, uint currentDataGeneration, uint currentMetadataGeneration, ulong createdAt)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("The master key is exactly 32 bytes (specification 03 §3.1).", nameof(masterKey));
        }

        _masterKey = masterKey.ToArray();
        CurrentDataGeneration = currentDataGeneration;
        CurrentMetadataGeneration = currentMetadataGeneration;
        CreatedAt = createdAt;
    }

    /// <summary>The 32-byte master key.</summary>
    public ReadOnlySpan<byte> MasterKey => _masterKey;

    /// <summary>The current data-key generation.</summary>
    public uint CurrentDataGeneration { get; }

    /// <summary>The current metadata-key generation.</summary>
    public uint CurrentMetadataGeneration { get; }

    /// <summary>Creation time, milliseconds since the Unix epoch (specification 00 §7).</summary>
    public ulong CreatedAt { get; }

    /// <summary>Deliberately redacted (specification 03 §8).</summary>
    public override string ToString() => "key-bundle(redacted)";

    /// <summary>Zeroes the held master key.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);
}
