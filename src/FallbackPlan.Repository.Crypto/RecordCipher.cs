using System.Security.Cryptography;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Seals and opens record payloads under a per-blob key with AES-256-GCM
/// (specification 04 §5–§6; FR-ARCH-009, NFR-SEC-003). The format layer
/// assembles the nonce and AAD bytes; this type only encrypts, mirroring the
/// <see cref="KeyWrapping"/> seam.
/// </summary>
/// <remarks>
/// A failed tag is an expected outcome the blob reader must survive per
/// record — corruption is local (04 §7) — so <see cref="TryOpen"/> reports
/// it as a result and guarantees no partial plaintext escapes. The
/// <c>xchacha20-poly1305-v1</c> profile is not implemented: the platform has
/// no XChaCha20-Poly1305 and no second implementation exists to cross-verify
/// one (open question Q12); callers refuse profile 0x0002 rather than guess.
/// </remarks>
public static class RecordCipher
{
    /// <summary>The authentication tag length: 16 bytes.</summary>
    public const int TagLength = 16;

    /// <summary>Encrypts a record payload.</summary>
    public static void Seal(
        ReadOnlySpan<byte> blobKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        using var aes = new AesGcm(blobKey, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    /// <summary>
    /// Decrypts and authenticates a record payload. Returns
    /// <see langword="false"/> — with <paramref name="plaintext"/> zeroed —
    /// when authentication fails: the record is damaged or was moved to an
    /// ordinal or repository its AAD does not cover (specification 04 §6
    /// step 5; the failed plaintext is never usable).
    /// </summary>
    public static bool TryOpen(
        ReadOnlySpan<byte> blobKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        try
        {
            using var aes = new AesGcm(blobKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
    }
}
