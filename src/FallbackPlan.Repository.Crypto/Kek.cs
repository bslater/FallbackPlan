using System.Security.Cryptography;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The 32-byte key-encryption key derived from the passphrase (specification
/// 03 §2). It wraps and unwraps the key object and does nothing else; it is
/// never written down (specification 03 §8).
/// </summary>
public sealed class Kek : IDisposable
{
    private readonly byte[] _bytes;

    internal Kek(byte[] bytes) => _bytes = bytes;

    /// <summary>The key bytes, for the wrap and unwrap operations only.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Deliberately redacted (specification 03 §8).</summary>
    public override string ToString() => "kek(redacted)";

    /// <summary>Zeroes the held key material.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}
