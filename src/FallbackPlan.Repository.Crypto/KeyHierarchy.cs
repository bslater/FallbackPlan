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

    private readonly byte[]? _masterKey;
    private readonly RepositoryWriteCredential? _credential;

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

    private KeyHierarchy(RepositoryWriteCredential owned) => _credential = owned;

    /// <summary>
    /// Creates a write-only (format v2) hierarchy over a write credential
    /// (ADR-0042; specification 03 §9): metadata, signing, content-ID and
    /// key-ID derivations answer from the bundle; the data-key family does
    /// not exist and asking for it throws. The credential is cloned — the
    /// caller keeps responsibility for its own copy.
    /// </summary>
    public static KeyHierarchy ForWriteOnly(RepositoryWriteCredential credential)
    {
        var serialized = credential.ToBytes();
        try
        {
            return new KeyHierarchy(RepositoryWriteCredential.FromBytes(serialized));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    /// <summary>Whether this hierarchy is a write-only bundle rather than a master key.</summary>
    public bool WriteOnly => _credential is not null;

    /// <summary>The write-only repository's sealing public key.</summary>
    /// <exception cref="InvalidOperationException">The hierarchy is not write-only.</exception>
    public ReadOnlySpan<byte> SealingPublicKey =>
        _credential is { } credential
            ? credential.SealingPublicKey
            : throw new InvalidOperationException(Strings.KeyHierarchy_NotWriteOnly);

    /// <summary>Derives the repository-scoped content-ID key (<c>"fbp/content-id/v1"</c>, or the v2 bundle's).</summary>
    public byte[] DeriveContentIdKey() =>
        _credential is { } credential ? credential.ContentIdKey.ToArray() : Expand("fbp/content-id/v1"u8, generation: null);

    /// <summary>Derives the repository-scoped key-ID key (<c>"fbp/key-id/v1"</c>, or the v2 bundle's).</summary>
    public byte[] DeriveKeyIdKey() =>
        _credential is { } credential ? credential.KeyIdKey.ToArray() : Expand("fbp/key-id/v1"u8, generation: null);

    /// <summary>Derives the data key for <paramref name="generation"/> (<c>"fbp/data/v1" ‖ u32(g)</c>).</summary>
    /// <exception cref="InvalidOperationException">
    /// The hierarchy is write-only: a v2 repository has no data-key family —
    /// content is sealed to its public key (specification 03 §9.2), and a
    /// code path asking for one is a bug, not a missing capability.
    /// </exception>
    public byte[] DeriveDataKey(KeyGeneration generation) =>
        _credential is null
            ? Expand("fbp/data/v1"u8, generation.Value)
            : throw new InvalidOperationException(Strings.KeyHierarchy_WriteOnlyHoldsNoDataKey);

    /// <summary>Derives the metadata key for <paramref name="generation"/> (<c>"fbp/metadata/v1" ‖ u32(g)</c>, or the v2 bundle's).</summary>
    public byte[] DeriveMetadataKey(KeyGeneration generation) =>
        _credential is { } credential
            ? credential.DeriveMetadataKey(generation)
            : Expand("fbp/metadata/v1"u8, generation.Value);

    /// <summary>
    /// Derives the signing-key seed for <paramref name="generation"/>
    /// (<c>"fbp/signing/v1" ‖ u32(g)</c>, or the v2 bundle's). The 32 bytes
    /// are an Ed25519 private-key seed per RFC 8032 §5.1.5 — the input to
    /// seed expansion, not a pre-clamped scalar (specification 03 §4;
    /// ADR-0020).
    /// </summary>
    public byte[] DeriveSigningKeySeed(KeyGeneration generation) =>
        _credential is { } credential
            ? credential.DeriveSigningKeySeed(generation)
            : Expand("fbp/signing/v1"u8, generation.Value);

    /// <summary>Zeroes the held key material.</summary>
    public void Dispose()
    {
        if (_masterKey is not null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
        }

        _credential?.Dispose();
    }

    private byte[] Expand(ReadOnlySpan<byte> label, uint? generation)
    {
        Span<byte> info = stackalloc byte[label.Length + (generation.HasValue ? 4 : 0)];
        label.CopyTo(info);

        if (generation.HasValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(info[label.Length..], generation.Value);
        }

        var derived = new byte[DerivedKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, _masterKey!, derived, info);

        return derived;
    }
}
