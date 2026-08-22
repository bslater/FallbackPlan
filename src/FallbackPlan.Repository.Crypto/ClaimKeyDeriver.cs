using System.Security.Cryptography;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The keypair that proves a machine holds a repository's passphrase, so a
/// destination may re-point that repository's replica to its current identity
/// (peer-protocol 07 §5.2; ADR-0046): <c>HKDF-Expand(claim_root,
/// "fbp/peer-claim/v1" ‖ claim_token, 32)</c>, read as an RFC 8032 §5.1.5
/// Ed25519 private-key seed.
/// </summary>
/// <remarks>
/// <para>
/// <c>claim_root</c> is the Argon2id output both repository formats already
/// derive from the passphrase and the descriptor's public salt — the KEK of a
/// v1 repository, the root of a v2 one, and the same <see cref="KekDerivation"/>
/// either way. That is why the label carries no format suffix: one claim path
/// serves both.
/// </para>
/// <para>
/// <c>claim_token</c> is minted by the <em>destination</em>, and it is not a
/// secret. Its job is uniqueness: two destinations holding replicas of one
/// repository mint different tokens, so the derived keypairs differ and a
/// proof produced at one is inert at the other. Without it a single captured
/// proof would claim that repository everywhere it is stored.
/// </para>
/// <para>
/// This is deliberately not the repository signing key. That key signs
/// manifests, and reusing a document-signing key as a network authentication
/// key is the cross-protocol reuse domain separation exists to prevent
/// (specification peer-protocol 00 §4).
/// </para>
/// </remarks>
public static class ClaimKeyDeriver
{
    /// <summary>The seed and public key length: 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>A destination's claim token is 16 bytes.</summary>
    public const int TokenLength = 16;

    private static ReadOnlySpan<byte> Label => "fbp/peer-claim/v1"u8;

    /// <summary>
    /// Derives the claim seed for one repository at one destination. The
    /// caller owns the returned bytes and should zero them.
    /// </summary>
    /// <param name="claimRoot">The Argon2id root derived from the passphrase, 32 bytes.</param>
    /// <param name="claimToken">The destination's own token for this replica, 16 bytes.</param>
    /// <exception cref="ArgumentException">Either input is the wrong length.</exception>
    public static byte[] DeriveSeed(ReadOnlySpan<byte> claimRoot, ReadOnlySpan<byte> claimToken)
    {
        if (claimRoot.Length != KeyLength)
        {
            throw new ArgumentException(Strings.ClaimKeyDeriver_ClaimRootExactlyBytes, nameof(claimRoot));
        }

        if (claimToken.Length != TokenLength)
        {
            throw new ArgumentException(Strings.ClaimKeyDeriver_ClaimTokenExactlyBytes, nameof(claimToken));
        }

        Span<byte> info = stackalloc byte[Label.Length + TokenLength];
        Label.CopyTo(info);
        claimToken.CopyTo(info[Label.Length..]);

        var seed = new byte[KeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, claimRoot, seed, info);
        return seed;
    }

    /// <summary>
    /// Computes the public half a destination stores and later compares
    /// against. Only this half ever leaves the claimant.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="seed"/> is not exactly 32 bytes.</exception>
    public static byte[] PublicKeyOf(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != KeyLength)
        {
            throw new ArgumentException(Strings.ClaimKeyDeriver_ClaimSeedExactlyBytes, nameof(seed));
        }

        using var key = Bodu.Security.Cryptography.Ed25519.Create();
        key.ImportPrivateKey(seed);
        return key.ExportPublicKey();
    }
}
