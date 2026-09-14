using Bodu;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Repository-scoped Ed25519 signing for one key generation
/// (specification 06 §6.1, 03 §4; ADR-0020): the seed is
/// <c>HKDF-Expand(master, "fbp/signing/v1" ‖ u32(g), 32)</c>, interpreted as
/// an RFC 8032 §5.1.5 private-key seed, and the public key is computed from
/// it — the format stores no public key object, so any reader entitled to
/// verify derives the keypair itself.
/// </summary>
/// <remarks>
/// A signature proves "a holder of the master key at generation <em>g</em>
/// produced this" and no more — device attribution is by claim (ADR-0020).
/// The underlying implementation applies strict cofactorless verification
/// (rejecting small-order points and non-canonical encodings); every
/// signature this type produces verifies under it, and the strictness only
/// narrows the accepted-forgery surface (ADR-0022 §Decision 8).
/// </remarks>
public sealed class RepositorySigner : IDisposable
{
    /// <summary>Ed25519 signatures are 64 bytes, R ‖ S.</summary>
    public const int SignatureLength = 64;

    /// <summary>Ed25519 public keys are 32 bytes.</summary>
    public const int PublicKeyLength = 32;

    private readonly Ed25519 _key;
    private readonly byte[] _publicKey;

    private RepositorySigner(Ed25519 key, KeyGeneration generation)
    {
        _key = key;
        _publicKey = key.ExportPublicKey();
        Generation = generation;
    }

    /// <summary>The key generation this signer serves.</summary>
    public KeyGeneration Generation { get; }

    /// <summary>The derived public key — computed, never stored (ADR-0020 §3).</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>
    /// Creates a signer for <paramref name="generation"/> from the hierarchy's
    /// derived seed.
    /// </summary>
    public static RepositorySigner Create(KeyHierarchy hierarchy, KeyGeneration generation)
    {
        ThrowHelper.ThrowIfNull(hierarchy);

        var seed = hierarchy.DeriveSigningKeySeed(generation);

        try
        {
            return FromSeed(seed, generation);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Creates a signer directly from a 32-byte RFC 8032 seed — the
    /// conformance-test entry point; production code goes through
    /// <see cref="Create"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="seed"/> is not exactly 32 bytes.</exception>
    public static RepositorySigner FromSeed(ReadOnlySpan<byte> seed, KeyGeneration generation)
    {
        var key = Ed25519.Create();

        try
        {
            key.ImportPrivateKey(seed);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new RepositorySigner(key, generation);
    }

    /// <summary>Signs <paramref name="message"/>, returning the 64-byte deterministic signature.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => _key.SignData(message);

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="message"/>
    /// against this generation's derived public key.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        signature.Length == SignatureLength && _key.VerifyData(message, signature);

    /// <inheritdoc />
    /// <summary>
    /// Verifies a signature against a bare public key — no seed, no private
    /// half ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5).
    /// </summary>
    /// <remarks>
    /// The one verification path that does not start inside the key boundary.
    /// A peer destination holds ciphertext and no repository keys, so it
    /// cannot derive a verifier the way ADR-0020 §3 assumes every reader can;
    /// what it holds is the public key recorded beside its attribution, and
    /// this is what it checks a deletion instruction with.
    /// </remarks>
    /// <param name="publicKey">The 32-byte Ed25519 public key.</param>
    /// <param name="signedBytes">The bytes the signature covers.</param>
    /// <param name="signature">The 64-byte signature.</param>
    /// <returns><see langword="true"/> when the signature verifies.</returns>
    public static bool VerifyWithPublicKey(
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        try
        {
            using var key = Ed25519.Create();
            key.ImportPublicKey(publicKey);
            return key.VerifyData(signedBytes, signature);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A public key the primitive will not import is a key that
            // verifies nothing, which is the honest answer rather than an
            // exception a caller would have to translate into one.
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}
