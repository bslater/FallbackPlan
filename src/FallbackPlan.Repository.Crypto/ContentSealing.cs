using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Seals a data blob's content key to the repository's X25519 public key and
/// opens it with the derived private key (ADR-0042 §2): a fresh ephemeral
/// keypair per seal, X25519 ECDH, HKDF-SHA256 binding both public shares,
/// AES-256-GCM over the content key. The nonce is fixed at zero because the
/// AEAD key is single-use by construction — a fresh ephemeral share per seal
/// means a fresh shared secret per seal.
/// </summary>
/// <remarks>
/// This is the format-critical X25519 use ADR-0019 Amendment 3 admits into
/// this assembly. The caller supplies the associated data that pins the
/// sealed share to its blob (repository id, blob id — specification 05's v2
/// envelope defines the exact bytes), so a share cannot be transplanted
/// between blobs without the open failing.
/// </remarks>
public static class ContentSealing
{
    /// <summary>An X25519 public share or scalar: 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>A sealed content key: ephemeral share ‖ ciphertext ‖ tag.</summary>
    public const int SealedLength = KeyLength + 32 + 16;

    private static readonly byte[] ZeroNonce = new byte[12];

    /// <summary>Seals <paramref name="contentKey"/> to <paramref name="recipientPublicKey"/>.</summary>
    /// <param name="recipientPublicKey">The repository's sealing public key.</param>
    /// <param name="contentKey">The 32-byte blob content key.</param>
    /// <param name="associatedData">The context that pins this share to its blob.</param>
    /// <exception cref="ArgumentException">A key is the wrong length.</exception>
    public static byte[] Seal(
        ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> associatedData)
    {
        if (recipientPublicKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_KeyExactlyBytes(KeyLength), nameof(recipientPublicKey));
        }

        if (contentKey.Length != 32)
        {
            throw new ArgumentException(Strings.FormatContentSealing_ContentKeyExactlyBytes(32), nameof(contentKey));
        }

        using var exchange = X25519.Create();
        exchange.GenerateKey();
        var ephemeralPublic = exchange.ExportPublicKey();
        var shared = exchange.DeriveSharedSecret(recipientPublicKey);
        var aeadKey = DeriveAeadKey(shared, ephemeralPublic, recipientPublicKey);
        try
        {
            var sealedBytes = new byte[SealedLength];
            ephemeralPublic.CopyTo(sealedBytes, 0);
            using var aead = new AesGcm(aeadKey, tagSizeInBytes: 16);
            aead.Encrypt(
                ZeroNonce, contentKey, sealedBytes.AsSpan(KeyLength, 32), sealedBytes.AsSpan(KeyLength + 32, 16),
                associatedData);
            return sealedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    /// <summary>Opens a sealed content key with the derived private scalar.</summary>
    /// <param name="sealingPrivateKey">The repository's derived X25519 scalar.</param>
    /// <param name="sealedBytes">The sealed share as written by <see cref="Seal"/>.</param>
    /// <param name="associatedData">The same context the seal was pinned to.</param>
    /// <exception cref="ArgumentException">A length is wrong, or the ephemeral share is a low-order point.</exception>
    /// <exception cref="SealedContentException">
    /// The share does not open — wrong repository, wrong context, or tampered
    /// bytes, deliberately indistinguishable.
    /// </exception>
    public static byte[] Open(
        ReadOnlySpan<byte> sealingPrivateKey, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associatedData)
    {
        if (sealingPrivateKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_KeyExactlyBytes(KeyLength), nameof(sealingPrivateKey));
        }

        if (sealedBytes.Length != SealedLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_SealedExactlyBytes(SealedLength), nameof(sealedBytes));
        }

        var ephemeralPublic = sealedBytes[..KeyLength];

        // A low-order share forces the shared secret to an attacker-known
        // value; refusing it here mirrors the pairing ceremony's guard.
        if (X25519.IsLowOrderPoint(ephemeralPublic))
        {
            throw new ArgumentException(Strings.ContentSealing_LowOrderShare, nameof(sealedBytes));
        }

        using var exchange = X25519.Create();
        exchange.ImportPrivateKey(sealingPrivateKey);
        var recipientPublic = exchange.ExportPublicKey();
        var shared = exchange.DeriveSharedSecret(ephemeralPublic);
        var aeadKey = DeriveAeadKey(shared, ephemeralPublic, recipientPublic);
        try
        {
            var contentKey = new byte[32];
            using var aead = new AesGcm(aeadKey, tagSizeInBytes: 16);
            try
            {
                aead.Decrypt(
                    ZeroNonce, sealedBytes.Slice(KeyLength, 32), sealedBytes.Slice(KeyLength + 32, 16), contentKey,
                    associatedData);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(contentKey);
                throw new SealedContentException(Strings.ContentSealing_DoesNotOpen);
            }

            return contentKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    private static byte[] DeriveAeadKey(
        ReadOnlySpan<byte> shared, ReadOnlySpan<byte> ephemeralPublic, ReadOnlySpan<byte> recipientPublic)
    {
        // Extract-then-expand: an ECDH output is a curve point's coordinate,
        // not uniform bytes, so unlike the master-key hierarchy the extract
        // step is NOT omitted here. The salt binds both public shares, so a
        // share swapped for another repository's derives a different key.
        Span<byte> salt = stackalloc byte[KeyLength * 2];
        ephemeralPublic.CopyTo(salt);
        recipientPublic.CopyTo(salt[KeyLength..]);

        var aeadKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, aeadKey, salt, "fbp/seal-content/v2"u8);
        return aeadKey;
    }
}

/// <summary>
/// A sealed content key that does not open — wrong repository, wrong
/// context, or tampered bytes, deliberately indistinguishable (the same
/// posture as <see cref="KeyUnwrapFailedException"/>).
/// </summary>
public sealed class SealedContentException(string message) : Exception(message);
