using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Derives a write-only repository's entire key material from its one
/// passphrase (ADR-0042 §1). <c>root = Argon2id(passphrase, salt, params)</c>;
/// every key is an independent one-way HKDF-SHA256 domain of that root —
/// <c>"fbp/seal/v2"</c> for the X25519 sealing scalar, <c>"fbp/metadata/v2"</c>
/// and <c>"fbp/signing/v2"</c> for the sub-roots the write credential expands
/// per generation, <c>"fbp/content-id/v2"</c> and <c>"fbp/key-id/v2"</c> for
/// the repository-scoped keys, and <c>"fbp/claim/v2"</c> for the installation's
/// claim key (ADR-0053 §1). Nothing is stored: the same passphrase and
/// salt always reproduce the same bundle, which is the whole design — and
/// why a v2 passphrase can never change.
/// </summary>
public static class WriteOnlyDerivation
{
    /// <summary>The X25519 key length: 32 bytes.</summary>
    public const int SealingKeyLength = 32;

    /// <summary>The Ed25519 reclaim seed length: 32 bytes (ADR-0055 §1).</summary>
    public const int ReclaimKeyLength = 32;

    /// <summary>The Ed25519 claim seed length: 32 bytes (ADR-0053 §1).</summary>
    public const int ClaimKeyLength = 32;

    /// <summary>
    /// Derives the full read authority — the write credential plus the
    /// sealing private key. Callers that only provision a service should
    /// take <see cref="RepositoryReadAuthority.Credential"/> and dispose the
    /// authority; restore holds the whole thing for the grant's life.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The salt is not exactly 16 bytes, or creation-mode parameters fall
    /// below the mandated minimums (via <see cref="KekDerivation"/>'s rules).
    /// </exception>
    public static RepositoryReadAuthority Derive(
        Passphrase passphrase,
        KdfParameters parameters,
        ReadOnlySpan<byte> salt,
        KdfValidationMode mode)
    {
        // The KDF, its validation modes and its minimums are exactly v1's
        // (specification 03 §2): only what the output feeds differs.
        using var derivation = KekDerivation.Derive(passphrase, parameters, salt, mode);
        return FromRoot(derivation.Kek.Bytes);
    }

    /// <summary>
    /// Expands an already-derived 32-byte root into the full authority —
    /// the second half of <see cref="Derive"/>, exposed so conformance
    /// vectors (which pin the root, Argon2id having no independent
    /// implementation in the vector generator) can exercise the expansion
    /// tree in isolation (specification 03 §9.1).
    /// </summary>
    /// <exception cref="ArgumentException">The root is not exactly 32 bytes.</exception>
    public static RepositoryReadAuthority FromRoot(ReadOnlySpan<byte> root)
    {
        if (root.Length != KekDerivation.KekLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_RootExactlyBytes(KekDerivation.KekLength), nameof(root));
        }

        var sealingScalar = Expand(root, "fbp/seal/v2"u8);
        byte[] sealingPublic;
        try
        {
            using var exchange = X25519.Create();
            exchange.ImportPrivateKey(sealingScalar);
            sealingPublic = exchange.ExportPublicKey();
        }
        catch
        {
            CryptographicOperations.ZeroMemory(sealingScalar);
            throw;
        }

        // The reclaim root sits beside the sealing scalar and NOT inside the
        // credential, which is the whole of ADR-0055 §2. The credential is
        // what a service is provisioned with and what crosses a process
        // boundary to get there; putting the reclaim domain in it would mean
        // withholding the key in name only.
        var reclaimSeed = Expand(root, "fbp/reclaim/v2"u8);

        // The claim key, on the same terms and for the same reason
        // ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1).
        // Derived from the INSTALLATION root rather than any repository's
        // master key, because a machine claiming a replica has lost the
        // repository: what it still holds is an installation kit, which names
        // no repository and carries no key object, so a repository-derived
        // claim key would be unreachable at exactly the moment it is needed.
        //
        // Not expanded per generation, unlike every other key here. A
        // destination records the claim public key at first attribution and
        // never replaces it, so a key that turned over would go stale with no
        // way to say so — and an installation root knows nothing of any one
        // repository's generations anyway.
        var claimSeed = Expand(root, "fbp/claim/v2"u8);

        RepositoryWriteCredential credential;
        try
        {
            // The PUBLIC half goes into the bundle and the private half does
            // not (ADR-0055 §2, §5). A public key authorises nothing, and a
            // service that cannot derive the private half still has to tell a
            // keyless destination which key its deletion instructions will be
            // signed under.
            byte[] reclaimPublic;
            using (var signer = RepositorySigner.FromSeed(
                DeriveReclaimKeySeed(reclaimSeed, KeyGeneration.Zero), KeyGeneration.Zero))
            {
                reclaimPublic = signer.PublicKey.ToArray();
            }

            byte[] claimPublic;
            using (var signer = RepositorySigner.FromSeed(claimSeed, KeyGeneration.Zero))
            {
                claimPublic = signer.PublicKey.ToArray();
            }

            credential = new RepositoryWriteCredential(
                sealingPublic,
                Expand(root, "fbp/content-id/v2"u8),
                Expand(root, "fbp/key-id/v2"u8),
                Expand(root, "fbp/metadata/v2"u8),
                Expand(root, "fbp/signing/v2"u8),
                reclaimPublic,
                claimPublic);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(sealingScalar);
            CryptographicOperations.ZeroMemory(reclaimSeed);
            CryptographicOperations.ZeroMemory(claimSeed);
            throw;
        }

        return new RepositoryReadAuthority(credential, sealingScalar, reclaimSeed, claimSeed);
    }

    /// <summary>
    /// Expands the reclaim sub-root into the Ed25519 seed for one generation —
    /// <c>"fbp/reclaim-generation/v2" ‖ u32(g)</c>, the same shape the write
    /// credential uses for its signing sub-root
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §1, §6).
    /// </summary>
    /// <remarks>
    /// Static and taking the root as a span rather than living on
    /// <see cref="RepositoryWriteCredential"/>, because the credential is
    /// precisely the thing that must never hold this root. A collection run
    /// on a write-only repository reaches it through a grant and nowhere
    /// else.
    /// </remarks>
    /// <param name="reclaimRoot">The 32-byte reclaim sub-root, from a grant or a derivation.</param>
    /// <param name="generation">The key generation in force.</param>
    /// <exception cref="ArgumentException">The root is not exactly 32 bytes.</exception>
    public static byte[] DeriveReclaimKeySeed(ReadOnlySpan<byte> reclaimRoot, KeyGeneration generation)
    {
        if (reclaimRoot.Length != ReclaimKeyLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_ReclaimSeedExactlyBytes(ReclaimKeyLength),
                nameof(reclaimRoot));
        }

        var derived = new byte[RepositoryWriteCredential.DerivedKeyLength];
        Span<byte> info = stackalloc byte[26 + sizeof(uint)];
        "fbp/reclaim-generation/v2"u8.CopyTo(info);
        var labelLength = "fbp/reclaim-generation/v2"u8.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            info[labelLength..(labelLength + sizeof(uint))], generation.Value);
        HKDF.Expand(HashAlgorithmName.SHA256, reclaimRoot, derived, info[..(labelLength + sizeof(uint))]);
        return derived;
    }

    private static byte[] Expand(ReadOnlySpan<byte> root, ReadOnlySpan<byte> info)
    {
        var derived = new byte[RepositoryWriteCredential.DerivedKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, root, derived, info);
        return derived;
    }
}

/// <summary>
/// A write credential together with the three secrets it deliberately does
/// not carry — the sealing private key that opens content (ADR-0042 §5), the
/// reclaim seed that authorises deletion
/// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §2), and the claim
/// seed that re-points a replica's attribution
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1).
/// The shape a
/// restore holds for exactly as long as its grant lives, and the shape setup
/// holds for exactly as long as provisioning takes. Owns and disposes every
/// part.
/// </summary>
/// <remarks>
/// The withheld secrets are different powers and are kept apart on purpose:
/// one reads the backups, one deletes them, and one decides which machine a
/// peer will hand them back to. A grant for any of them must not silently
/// confer another — and the claim seed is granted to nobody at all.
/// </remarks>
public sealed class RepositoryReadAuthority : IDisposable
{
    private readonly byte[] _sealingPrivateKey;
    private readonly byte[] _reclaimKeySeed;
    private readonly byte[] _claimKeySeed;

    internal RepositoryReadAuthority(
        RepositoryWriteCredential credential,
        byte[] sealingPrivateKey,
        byte[] reclaimKeySeed,
        byte[]? claimKeySeed = null)
    {
        Credential = credential;
        _sealingPrivateKey = sealingPrivateKey;
        _reclaimKeySeed = reclaimKeySeed;
        _claimKeySeed = claimKeySeed ?? [];
    }

    /// <summary>
    /// Rebuilds an authority from its two parts — the service's stored write
    /// credential and a granted scalar that arrived sealed (ADR-0042 §5).
    /// Both are cloned; the caller keeps responsibility for its own copies.
    /// </summary>
    /// <param name="credential">The service's stored write bundle.</param>
    /// <param name="sealingPrivateKey">The granted X25519 scalar.</param>
    /// <param name="reclaimKeySeed">
    /// The granted reclaim seed, or empty when the grant conveys read
    /// authority alone. A restore grant does not carry it and must not: the
    /// power to read is not the power to delete (ADR-0055 §6).
    /// </param>
    public static RepositoryReadAuthority FromParts(
        RepositoryWriteCredential credential,
        ReadOnlySpan<byte> sealingPrivateKey,
        ReadOnlySpan<byte> reclaimKeySeed = default)
    {
        if (sealingPrivateKey.Length != WriteOnlyDerivation.SealingKeyLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_RootExactlyBytes(WriteOnlyDerivation.SealingKeyLength),
                nameof(sealingPrivateKey));
        }

        if (!reclaimKeySeed.IsEmpty && reclaimKeySeed.Length != WriteOnlyDerivation.ReclaimKeyLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_ReclaimSeedExactlyBytes(
                    WriteOnlyDerivation.ReclaimKeyLength),
                nameof(reclaimKeySeed));
        }

        var serialized = credential.ToBytes();
        try
        {
            return new RepositoryReadAuthority(
                RepositoryWriteCredential.FromBytes(serialized),
                sealingPrivateKey.ToArray(),
                reclaimKeySeed.ToArray());
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(serialized);
        }
    }

    /// <summary>The write bundle.</summary>
    public RepositoryWriteCredential Credential { get; }

    /// <summary>The X25519 scalar that opens sealed content keys.</summary>
    public ReadOnlySpan<byte> SealingPrivateKey => _sealingPrivateKey;

    /// <summary>
    /// The Ed25519 seed a tombstone signs under (ADR-0055 §1) — empty when
    /// this authority conveys read access alone.
    /// </summary>
    public ReadOnlySpan<byte> ReclaimKeySeed => _reclaimKeySeed;

    /// <summary>
    /// The Ed25519 seed a claim signs under (ADR-0053 §1) — empty unless this
    /// authority came from a passphrase, because no grant carries it.
    /// </summary>
    /// <remarks>
    /// Deliberately not grantable. A claim re-points which device a peer will
    /// serve a replica to, and the person who may decide that is the one
    /// holding the passphrase and the installation kit — not a service, and
    /// not a run.
    /// </remarks>
    public ReadOnlySpan<byte> ClaimKeySeed => _claimKeySeed;

    /// <summary>Deliberately redacted.</summary>
    public override string ToString() => "read-authority(redacted)";

    /// <summary>Zeroes both withheld secrets and disposes the credential.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_sealingPrivateKey);
        CryptographicOperations.ZeroMemory(_reclaimKeySeed);
        CryptographicOperations.ZeroMemory(_claimKeySeed);
        Credential.Dispose();
    }
}
