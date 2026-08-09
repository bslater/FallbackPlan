using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives every repository key from the master key via HKDF-Expand with
/// domain-separated info strings (specification 03 §4; FR-ARCH-008). The
/// master key is used directly as the PRK — the extract step is deliberately
/// omitted because the master key is already uniform random — and it never
/// encrypts anything itself.
/// </summary>
/// <remarks>
/// Info strings are ASCII without a terminating NUL; generation numbers are
/// appended big-endian. All primitives here are platform-provided
/// (<see cref="HKDF"/> over HMAC-SHA256); no third-party code is involved.
/// </remarks>
public sealed class KeyHierarchy : IDisposable
{
    /// <summary>Every derived key is 32 bytes.</summary>
    public const int DerivedKeyLength = 32;

    /// <summary>The master key is 32 bytes (specification 03 §3.1).</summary>
    public const int MasterKeyLength = 32;

    private readonly byte[] _masterKey;

    /// <summary>
    /// Creates the hierarchy over a 32-byte master key. The bytes are copied;
    /// the caller keeps responsibility for its own copy.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="masterKey"/> is not exactly 32 bytes.</exception>
    public KeyHierarchy(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != MasterKeyLength)
        {
            throw new ArgumentException(Strings.FormatKeyHierarchy_MasterKeyExactlyBytes(MasterKeyLength), nameof(masterKey));
        }

        _masterKey = masterKey.ToArray();
    }

    /// <summary>Derives the repository-scoped content-ID key (<c>"fbp/content-id/v1"</c>).</summary>
    public byte[] DeriveContentIdKey() => Expand("fbp/content-id/v1"u8, generation: null);

    /// <summary>Derives the repository-scoped key-ID key (<c>"fbp/key-id/v1"</c>).</summary>
    public byte[] DeriveKeyIdKey() => Expand("fbp/key-id/v1"u8, generation: null);

    /// <summary>Derives the data key for <paramref name="generation"/> (<c>"fbp/data/v1" ‖ u32(g)</c>).</summary>
    public byte[] DeriveDataKey(KeyGeneration generation) => Expand("fbp/data/v1"u8, generation.Value);

    /// <summary>Derives the metadata key for <paramref name="generation"/> (<c>"fbp/metadata/v1" ‖ u32(g)</c>).</summary>
    public byte[] DeriveMetadataKey(KeyGeneration generation) => Expand("fbp/metadata/v1"u8, generation.Value);

    /// <summary>
    /// Derives the signing-key seed for <paramref name="generation"/>
    /// (<c>"fbp/signing/v1" ‖ u32(g)</c>). The 32 bytes are an Ed25519
    /// private-key seed per RFC 8032 §5.1.5 — the input to seed expansion, not
    /// a pre-clamped scalar (specification 03 §4; ADR-0020).
    /// </summary>
    public byte[] DeriveSigningKeySeed(KeyGeneration generation) => Expand("fbp/signing/v1"u8, generation.Value);

    /// <summary>Zeroes the held master-key copy.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);

    private byte[] Expand(ReadOnlySpan<byte> label, uint? generation)
    {
        Span<byte> info = stackalloc byte[label.Length + (generation.HasValue ? 4 : 0)];
        label.CopyTo(info);

        if (generation.HasValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(info[label.Length..], generation.Value);
        }

        var derived = new byte[DerivedKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, _masterKey, derived, info);

        return derived;
    }
}
