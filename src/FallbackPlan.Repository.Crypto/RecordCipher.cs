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
/// it as a result and guarantees no partial plaintext escapes. Format
/// version 1 has one record AEAD (03 §6): profile <c>0x0002</c> is reserved
/// and never assigned, and callers refuse it rather than guess.
/// </remarks>
public static class RecordCipher
{
    /// <summary>The authentication tag length: 16 bytes.</summary>
    public const int TagLength = 16;

    /// <summary>Encrypts a record payload.</summary>
    /// <param name="blobKey">The blob key.</param>
    /// <param name="nonce">The record nonce — the ordinal (ADR-0005).</param>
    /// <param name="aad">The associated data binding the record's position.</param>
    /// <param name="plaintext">The stored payload.</param>
    /// <param name="ciphertext">Where the ciphertext goes.</param>
    /// <param name="tag">Where the tag goes.</param>
    /// <remarks>
    /// Constructs a cipher per call, which pays an AES key schedule per record.
    /// Callers that seal many records under one key should hold an
    /// <see cref="AesGcm"/> and call <see cref="Seal(AesGcm, ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte}, Span{byte})"/>
    /// instead (ADR-0029 §6, serial cost 3).
    /// </remarks>
    public static void Seal(
        ReadOnlySpan<byte> blobKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        using var aes = new AesGcm(blobKey, TagLength);
        Seal(aes, nonce, aad, plaintext, ciphertext, tag);
    }

    /// <summary>Encrypts a record payload under an already-scheduled key.</summary>
    /// <param name="cipher">
    /// A cipher holding the blob key. <see cref="AesGcm"/> is not safe for
    /// concurrent use, so the caller must keep it inside the ordered stage —
    /// which is where record sealing lives anyway (ADR-0029 §1).
    /// </param>
    /// <param name="nonce">The record nonce — the ordinal (ADR-0005).</param>
    /// <param name="aad">The associated data binding the record's position.</param>
    /// <param name="plaintext">The stored payload.</param>
    /// <param name="ciphertext">Where the ciphertext goes.</param>
    /// <param name="tag">Where the tag goes.</param>
    public static void Seal(
        AesGcm cipher,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, aad);
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
