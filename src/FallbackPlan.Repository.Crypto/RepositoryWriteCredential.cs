using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The write bundle of a write-only repository (ADR-0042 §1): everything the
/// service holds for a format-v2 set — the sealing public key, the
/// content-ID and key-ID keys, and the structure and signing sub-roots that
/// expand into per-generation keys. Nothing in here opens file content, and
/// nothing in here walks back to the passphrase, the derivation root, or the
/// sealing private key (NFR-SEC-010): every member is an independent one-way
/// HKDF-SHA256 output.
/// </summary>
/// <remarks>
/// Per-generation keys derive from the sub-roots rather than being stored,
/// because generations are unbounded; the sub-roots are themselves one-way
/// domains of the root, so holding them yields exactly the generation keys
/// and nothing beside. The serialised form exists for the service's
/// credential store and the provisioning envelope — it is key material and is
/// treated as such wherever it rests (owner-only, platform keystore).
/// </remarks>
public sealed class RepositoryWriteCredential : IDisposable
{
    /// <summary>The serialised length: magic plus five 32-byte members.</summary>
    public const int SerializedLength = 8 + (5 * 32);

    private static readonly byte[] Magic = "FBPWCRD1"u8.ToArray();

    private readonly byte[] _sealingPublicKey;
    private readonly byte[] _contentIdKey;
    private readonly byte[] _keyIdKey;
    private readonly byte[] _structureRoot;
    private readonly byte[] _signingRoot;

    internal RepositoryWriteCredential(
        byte[] sealingPublicKey, byte[] contentIdKey, byte[] keyIdKey, byte[] structureRoot, byte[] signingRoot)
    {
        _sealingPublicKey = sealingPublicKey;
        _contentIdKey = contentIdKey;
        _keyIdKey = keyIdKey;
        _structureRoot = structureRoot;
        _signingRoot = signingRoot;
    }

    /// <summary>The X25519 public key file contents are sealed to.</summary>
    public ReadOnlySpan<byte> SealingPublicKey => _sealingPublicKey;

    /// <summary>The repository-scoped content-ID key.</summary>
    public ReadOnlySpan<byte> ContentIdKey => _contentIdKey;

    /// <summary>The repository-scoped key-ID key.</summary>
    public ReadOnlySpan<byte> KeyIdKey => _keyIdKey;

    /// <summary>
    /// Derives the structure-plane (metadata-class) key for
    /// <paramref name="generation"/> — <c>"fbp/metadata-generation/v2" ‖ u32(g)</c>
    /// over the structure sub-root.
    /// </summary>
    public byte[] DeriveMetadataKey(KeyGeneration generation) =>
        Expand(_structureRoot, "fbp/metadata-generation/v2"u8, generation.Value);

    /// <summary>
    /// Derives the Ed25519 signing seed for <paramref name="generation"/> —
    /// <c>"fbp/signing-generation/v2" ‖ u32(g)</c> over the signing sub-root
    /// (RFC 8032 §5.1.5 seed semantics, exactly as v1's — ADR-0020).
    /// </summary>
    public byte[] DeriveSigningKeySeed(KeyGeneration generation) =>
        Expand(_signingRoot, "fbp/signing-generation/v2"u8, generation.Value);

    /// <summary>The serialised credential, for the store and the sealed envelope.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[SerializedLength];
        Magic.CopyTo(bytes, 0);
        _sealingPublicKey.CopyTo(bytes, 8);
        _contentIdKey.CopyTo(bytes, 40);
        _keyIdKey.CopyTo(bytes, 72);
        _structureRoot.CopyTo(bytes, 104);
        _signingRoot.CopyTo(bytes, 136);
        return bytes;
    }

    /// <summary>Parses a serialised credential.</summary>
    /// <exception cref="ArgumentException">The bytes are not a serialised write credential.</exception>
    public static RepositoryWriteCredential FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SerializedLength || !bytes[..8].SequenceEqual(Magic))
        {
            throw new ArgumentException(
                Strings.FormatRepositoryWriteCredential_SerialisedExactlyBytes(SerializedLength), nameof(bytes));
        }

        return new RepositoryWriteCredential(
            bytes[8..40].ToArray(),
            bytes[40..72].ToArray(),
            bytes[72..104].ToArray(),
            bytes[104..136].ToArray(),
            bytes[136..168].ToArray());
    }

    /// <summary>Deliberately redacted.</summary>
    public override string ToString() => "write-credential(redacted)";

    /// <summary>Zeroes every held member.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_sealingPublicKey);
        CryptographicOperations.ZeroMemory(_contentIdKey);
        CryptographicOperations.ZeroMemory(_keyIdKey);
        CryptographicOperations.ZeroMemory(_structureRoot);
        CryptographicOperations.ZeroMemory(_signingRoot);
    }

    private static byte[] Expand(byte[] prk, ReadOnlySpan<byte> label, uint generation)
    {
        Span<byte> info = stackalloc byte[label.Length + 4];
        label.CopyTo(info);
        BinaryPrimitives.WriteUInt32BigEndian(info[label.Length..], generation);

        var derived = new byte[KeyHierarchy.DerivedKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, derived, info);
        return derived;
    }
}
