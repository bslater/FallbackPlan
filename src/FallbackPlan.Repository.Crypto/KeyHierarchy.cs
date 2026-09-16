using System.Security.Cryptography;
using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The repository's key hierarchy over its write credential (ADR-0042;
/// specification 03 §9): the metadata, signing, content-ID and key-ID
/// derivations answer from the bundle. There is no data-key family — content
/// is sealed to the repository's public key — and no reclaim or claim private
/// half: a service publishes for ever and cannot author a deletion or re-point
/// a replica (ADR-0055 §2, ADR-0053 §1); both authorities arrive as grants or
/// are derived where the person typed.
/// </summary>
/// <remarks>
/// All primitives are platform-provided (<see cref="HKDF"/> over
/// HMAC-SHA256); no third-party code is involved.
/// </remarks>
public sealed class KeyHierarchy : IDisposable
{
    /// <summary>Every derived key is 32 bytes.</summary>
    public const int DerivedKeyLength = 32;

    private readonly RepositoryWriteCredential _credential;

    private KeyHierarchy(RepositoryWriteCredential owned) => _credential = owned;

    /// <summary>
    /// Creates the hierarchy over a write credential. The credential is
    /// cloned — the caller keeps responsibility for its own copy.
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

    /// <summary>The repository's sealing public key — the wrong-passphrase verifier and what content seals to.</summary>
    public ReadOnlySpan<byte> SealingPublicKey => _credential.SealingPublicKey;

    /// <summary>The repository-scoped content-ID key.</summary>
    public byte[] DeriveContentIdKey() => _credential.ContentIdKey.ToArray();

    /// <summary>The repository-scoped key-ID key.</summary>
    public byte[] DeriveKeyIdKey() => _credential.KeyIdKey.ToArray();

    /// <summary>Derives the metadata key for <paramref name="generation"/> (<c>"fbp/metadata-generation/v2" ‖ u32(g)</c>).</summary>
    public byte[] DeriveMetadataKey(KeyGeneration generation) => _credential.DeriveMetadataKey(generation);

    /// <summary>
    /// Derives the signing-key seed for <paramref name="generation"/>
    /// (<c>"fbp/signing-generation/v2" ‖ u32(g)</c>). The 32 bytes are an
    /// Ed25519 private-key seed per RFC 8032 §5.1.5 — the input to seed
    /// expansion, not a pre-clamped scalar (ADR-0020).
    /// </summary>
    public byte[] DeriveSigningKeySeed(KeyGeneration generation) => _credential.DeriveSigningKeySeed(generation);

    /// <summary>
    /// The Ed25519 <b>public</b> half of the claim key, read off the write
    /// credential (ADR-0053 §1). Empty only for a credential written before
    /// the claim decision, whose replica is the case ADR-0053 §3 leaves to
    /// the destination's operator.
    /// </summary>
    public byte[] ClaimPublicKey() => _credential.ClaimPublicKey.ToArray();

    /// <summary>
    /// The Ed25519 <b>public</b> half of the reclaim key, read off the write
    /// credential ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5).
    /// Empty only for a credential written before the reclaim decision, which
    /// publishes nothing until it is re-provisioned.
    /// </summary>
    /// <param name="generation">The key generation in force; the credential's copy does not turn over with it.</param>
    public byte[] ReclaimPublicKey(KeyGeneration generation)
    {
        _ = generation;
        return _credential.ReclaimPublicKey.ToArray();
    }

    /// <summary>Zeroes the held key material.</summary>
    public void Dispose() => _credential.Dispose();
}
