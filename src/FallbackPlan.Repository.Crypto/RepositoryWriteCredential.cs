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
    /// <summary>The serialised length: magic plus six 32-byte members.</summary>
    public const int SerializedLength = 8 + (6 * 32);

    /// <summary>The length a credential written before the reclaim public key existed.</summary>
    internal const int LegacySerializedLength = 8 + (5 * 32);

    private static readonly byte[] Magic = "FBPWCRD2"u8.ToArray();

    /// <summary>
    /// The magic a pre-reclaim credential carries
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5).
    /// </summary>
    /// <remarks>
    /// Read but never written. A service provisioned before this decision has
    /// a bundle on disk with five members, and refusing to open it would take
    /// the set offline over a field it does not yet need — the reclaim public
    /// key is empty for such a credential, and the set publishes none to its
    /// peers until it is re-provisioned.
    /// </remarks>
    private static readonly byte[] LegacyMagic = "FBPWCRD1"u8.ToArray();

    private readonly byte[] _sealingPublicKey;
    private readonly byte[] _contentIdKey;
    private readonly byte[] _keyIdKey;
    private readonly byte[] _structureRoot;
    private readonly byte[] _signingRoot;
    private readonly byte[] _reclaimPublicKey;

    internal RepositoryWriteCredential(
        byte[] sealingPublicKey, byte[] contentIdKey, byte[] keyIdKey, byte[] structureRoot, byte[] signingRoot,
        byte[]? reclaimPublicKey = null)
    {
        _sealingPublicKey = sealingPublicKey;
        _contentIdKey = contentIdKey;
        _keyIdKey = keyIdKey;
        _structureRoot = structureRoot;
        _signingRoot = signingRoot;
        _reclaimPublicKey = reclaimPublicKey ?? [];
    }

    /// <summary>The X25519 public key file contents are sealed to.</summary>
    public ReadOnlySpan<byte> SealingPublicKey => _sealingPublicKey;

    /// <summary>
    /// The Ed25519 <b>public</b> half of the reclaim key
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §1) — empty on a
    /// credential written before the reclaim decision.
    /// </summary>
    /// <remarks>
    /// A public key confers no authority, so withholding it would buy nothing
    /// and cost the one thing it is for: a service that cannot derive the
    /// private half still has to be able to tell a keyless destination which
    /// key a deletion instruction will be signed under (§5). The private half
    /// is deliberately absent from this bundle and reaches a collection run
    /// only through a grant (§6).
    /// </remarks>
    public ReadOnlySpan<byte> ReclaimPublicKey => _reclaimPublicKey;

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

        // A credential read from a pre-reclaim bundle round-trips into the
        // current shape with this member left zero rather than refusing to
        // serialise: the service that holds it still works, and publishes no
        // reclaim public key until it is re-provisioned.
        _reclaimPublicKey.CopyTo(bytes, 168);
        return bytes;
    }

    /// <summary>Parses a serialised credential.</summary>
    /// <exception cref="ArgumentException">The bytes are not a serialised write credential.</exception>
    public static RepositoryWriteCredential FromBytes(ReadOnlySpan<byte> bytes)
    {
        var legacy = bytes.Length == LegacySerializedLength && bytes[..8].SequenceEqual(LegacyMagic);
        if (!legacy && (bytes.Length != SerializedLength || !bytes[..8].SequenceEqual(Magic)))
        {
            throw new ArgumentException(
                Strings.FormatRepositoryWriteCredential_SerialisedExactlyBytes(SerializedLength), nameof(bytes));
        }

        return new RepositoryWriteCredential(
            bytes[8..40].ToArray(),
            bytes[40..72].ToArray(),
            bytes[72..104].ToArray(),
            bytes[104..136].ToArray(),
            bytes[136..168].ToArray(),
            legacy ? null : bytes[168..200].ToArray());
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
        CryptographicOperations.ZeroMemory(_reclaimPublicKey);
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
