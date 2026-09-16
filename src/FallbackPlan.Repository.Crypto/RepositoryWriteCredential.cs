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
    /// <summary>The serialised length: magic plus seven 32-byte members.</summary>
    public const int SerializedLength = 8 + (7 * 32);

    /// <summary>The length a credential written before the claim public key existed.</summary>
    internal const int PreClaimSerializedLength = 8 + (6 * 32);

    /// <summary>The length a credential written before the reclaim public key existed.</summary>
    internal const int LegacySerializedLength = 8 + (5 * 32);

    private static readonly byte[] Magic = "FBPWCRD3"u8.ToArray();

    /// <summary>
    /// The magic a credential written before the claim public key carries
    /// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1).
    /// </summary>
    /// <remarks>
    /// Read and written, on the same rule as <see cref="LegacyMagic"/>: a set
    /// provisioned before the claim key publishes none, and its replica is
    /// the case ADR-0053 §3 leaves to the destination's operator.
    /// </remarks>
    private static readonly byte[] PreClaimMagic = "FBPWCRD2"u8.ToArray();

    /// <summary>
    /// The magic a pre-reclaim credential carries
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5).
    /// </summary>
    /// <remarks>
    /// A service provisioned before this decision has
    /// a bundle on disk with five members, and refusing to open it would take
    /// the set offline over a field it does not yet need — the reclaim public
    /// key is empty for such a credential, and the set publishes none to its
    /// peers until it is re-provisioned. Written as well as read, so that
    /// absence survives a round trip: see <see cref="ToBytes"/>.
    /// </remarks>
    private static readonly byte[] LegacyMagic = "FBPWCRD1"u8.ToArray();

    private readonly byte[] _sealingPublicKey;
    private readonly byte[] _contentIdKey;
    private readonly byte[] _keyIdKey;
    private readonly byte[] _structureRoot;
    private readonly byte[] _signingRoot;
    private readonly byte[] _reclaimPublicKey;
    private readonly byte[] _claimPublicKey;

    internal RepositoryWriteCredential(
        byte[] sealingPublicKey, byte[] contentIdKey, byte[] keyIdKey, byte[] structureRoot, byte[] signingRoot,
        byte[]? reclaimPublicKey = null, byte[]? claimPublicKey = null)
    {
        // The serialised shapes are nested — each adds a trailing member to
        // the one before — so a later member present while an earlier one is
        // absent has no representation. Nothing derives one that way; the
        // guard is here so that a future member added carelessly fails loudly
        // rather than writing a zero-filled slot that reads back as a key.
        if (claimPublicKey is { Length: > 0 } && reclaimPublicKey is not { Length: > 0 })
        {
            throw new ArgumentException(
                Strings.RepositoryWriteCredential_ClaimNeedsReclaim, nameof(claimPublicKey));
        }

        _sealingPublicKey = sealingPublicKey;
        _contentIdKey = contentIdKey;
        _keyIdKey = keyIdKey;
        _structureRoot = structureRoot;
        _signingRoot = signingRoot;
        _reclaimPublicKey = reclaimPublicKey ?? [];
        _claimPublicKey = claimPublicKey ?? [];
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

    /// <summary>
    /// The Ed25519 <b>public</b> half of the claim key
    /// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1)
    /// — empty on a credential written before the claim decision.
    /// </summary>
    /// <remarks>
    /// Published to a destination at first attribution so that a machine
    /// rebuilt after total loss can prove the replica is its own. The private
    /// half is deliberately absent, exactly as the reclaim key's is: a
    /// compromised service that could author a claim could re-point a
    /// replica's attribution to a machine of its choosing, and the claimant —
    /// who holds the passphrase and the kit — reaches the key by derivation
    /// rather than from anything a service stores.
    /// </remarks>
    public ReadOnlySpan<byte> ClaimPublicKey => _claimPublicKey;

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

    /// <summary>
    /// The serialised credential, for the store and the sealed envelope —
    /// <see cref="SerializedLength"/> bytes, or the shorter pre-reclaim shape
    /// for a credential that carries no reclaim public key.
    /// </summary>
    /// <remarks>
    /// Writing the shape this credential actually holds, rather than always
    /// the current one with the absent member left zero, is what makes an
    /// absence survive a round trip — and a round trip is not rare:
    /// <see cref="KeyHierarchy.ForWriteOnly"/> performs one on every open. A
    /// zero-filled member reads back as a 32-byte key rather than as nothing,
    /// which would put all-zero bytes on every <c>ReplicationOffer</c>, where
    /// a destination records them permanently and can never be told otherwise
    /// (ADR-0055 §5). The set would then owe signatures under a key whose
    /// private half does not exist.
    /// </remarks>
    public byte[] ToBytes()
    {
        var (magic, length) = _claimPublicKey.Length > 0
            ? (Magic, SerializedLength)
            : _reclaimPublicKey.Length > 0
                ? (PreClaimMagic, PreClaimSerializedLength)
                : (LegacyMagic, LegacySerializedLength);

        var bytes = new byte[length];
        magic.CopyTo(bytes, 0);
        _sealingPublicKey.CopyTo(bytes, 8);
        _contentIdKey.CopyTo(bytes, 40);
        _keyIdKey.CopyTo(bytes, 72);
        _structureRoot.CopyTo(bytes, 104);
        _signingRoot.CopyTo(bytes, 136);

        if (_reclaimPublicKey.Length > 0)
        {
            _reclaimPublicKey.CopyTo(bytes, 168);
        }

        if (_claimPublicKey.Length > 0)
        {
            _claimPublicKey.CopyTo(bytes, 200);
        }

        return bytes;
    }

    /// <summary>
    /// The length of the serialised credential at the front of
    /// <paramref name="bytes"/>, or <c>-1</c> when it is not one.
    /// </summary>
    /// <remarks>
    /// For the containers that embed a credential in a longer payload — the
    /// stored installation provisioning and the sealed provisioning envelope.
    /// Each one used to pin its own total length against a single credential
    /// length, so widening the credential silently made every bundle an older
    /// build had written unreadable: an installation refused its own
    /// credential as damage, and could not be re-provisioned, because saving
    /// deliberately never overwrites. Asking the credential how long it is
    /// keeps that from happening the next time a member is added.
    /// </remarks>
    /// <param name="bytes">A buffer beginning with a serialised credential.</param>
    public static int LengthOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return -1;
        }

        var length = bytes[..8] switch
        {
            var magic when magic.SequenceEqual(Magic) => SerializedLength,
            var magic when magic.SequenceEqual(PreClaimMagic) => PreClaimSerializedLength,
            var magic when magic.SequenceEqual(LegacyMagic) => LegacySerializedLength,
            _ => -1,
        };

        return length >= 0 && bytes.Length >= length ? length : -1;
    }

    /// <summary>Parses a serialised credential.</summary>
    /// <exception cref="ArgumentException">The bytes are not a serialised write credential.</exception>
    public static RepositoryWriteCredential FromBytes(ReadOnlySpan<byte> bytes)
    {
        var length = LengthOf(bytes);
        if (length < 0 || bytes.Length != length)
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
            length >= PreClaimSerializedLength ? bytes[168..200].ToArray() : null,
            length >= SerializedLength ? bytes[200..232].ToArray() : null);
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
        CryptographicOperations.ZeroMemory(_claimPublicKey);
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
