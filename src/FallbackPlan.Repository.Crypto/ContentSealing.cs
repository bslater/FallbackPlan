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

    /// <summary>
    /// The X25519 public key for a 32-byte private scalar. Exposed so hosts
    /// can mint envelope-recipient keypairs and prove granted scalars without
    /// touching the primitive themselves — ADR-0019 Amendment 3 confines
    /// X25519 to this assembly.
    /// </summary>
    /// <exception cref="ArgumentException">The scalar is not exactly 32 bytes.</exception>
    public static byte[] PublicKeyOf(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_KeyExactlyBytes(KeyLength), nameof(privateKey));
        }

        using var exchange = X25519.Create();
        exchange.ImportPrivateKey(privateKey);
        return exchange.ExportPublicKey();
    }

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

    /// <summary>
    /// Seals an arbitrary small payload to a recipient public key — the
    /// provisioning and restore-grant envelopes (ADR-0042 §4). The same
    /// construction as <see cref="Seal"/> with a payload-length ciphertext
    /// and its own HKDF info, so a content-key share and an envelope can
    /// never be confused for one another.
    /// </summary>
    public static byte[] SealPayload(
        ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> associatedData)
    {
        if (recipientPublicKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_KeyExactlyBytes(KeyLength), nameof(recipientPublicKey));
        }

        using var exchange = X25519.Create();
        exchange.GenerateKey();
        var ephemeralPublic = exchange.ExportPublicKey();
        var shared = exchange.DeriveSharedSecret(recipientPublicKey);
        var aeadKey = DeriveEnvelopeKey(shared, ephemeralPublic, recipientPublicKey);
        try
        {
            var sealedBytes = new byte[KeyLength + payload.Length + 16];
            ephemeralPublic.CopyTo(sealedBytes, 0);
            using var aead = new AesGcm(aeadKey, tagSizeInBytes: 16);
            aead.Encrypt(
                ZeroNonce, payload, sealedBytes.AsSpan(KeyLength, payload.Length),
                sealedBytes.AsSpan(KeyLength + payload.Length, 16), associatedData);
            return sealedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    /// <summary>Opens a payload envelope sealed by <see cref="SealPayload"/>.</summary>
    /// <exception cref="ArgumentException">A length is impossible, or the ephemeral share is a low-order point.</exception>
    /// <exception cref="SealedContentException">The envelope does not open — wrong recipient, wrong purpose, or tampered bytes.</exception>
    public static byte[] OpenPayload(
        ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associatedData)
    {
        if (recipientPrivateKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatContentSealing_KeyExactlyBytes(KeyLength), nameof(recipientPrivateKey));
        }

        if (sealedBytes.Length < KeyLength + 16)
        {
            throw new ArgumentException(Strings.FormatContentSealing_SealedExactlyBytes(KeyLength + 16), nameof(sealedBytes));
        }

        var ephemeralPublic = sealedBytes[..KeyLength];
        if (X25519.IsLowOrderPoint(ephemeralPublic))
        {
            throw new ArgumentException(Strings.ContentSealing_LowOrderShare, nameof(sealedBytes));
        }

        using var exchange = X25519.Create();
        exchange.ImportPrivateKey(recipientPrivateKey);
        var recipientPublic = exchange.ExportPublicKey();
        var shared = exchange.DeriveSharedSecret(ephemeralPublic);
        var aeadKey = DeriveEnvelopeKey(shared, ephemeralPublic, recipientPublic);
        try
        {
            var payload = new byte[sealedBytes.Length - KeyLength - 16];
            using var aead = new AesGcm(aeadKey, tagSizeInBytes: 16);
            try
            {
                aead.Decrypt(
                    ZeroNonce, sealedBytes.Slice(KeyLength, payload.Length), sealedBytes[^16..], payload,
                    associatedData);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(payload);
                throw new SealedContentException(Strings.ContentSealing_DoesNotOpen);
            }

            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    private static byte[] DeriveEnvelopeKey(
        ReadOnlySpan<byte> shared, ReadOnlySpan<byte> ephemeralPublic, ReadOnlySpan<byte> recipientPublic)
    {
        Span<byte> salt = stackalloc byte[KeyLength * 2];
        ephemeralPublic.CopyTo(salt);
        recipientPublic.CopyTo(salt[KeyLength..]);

        var aeadKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, aeadKey, salt, "fbp/envelope/v2"u8);
        return aeadKey;
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
