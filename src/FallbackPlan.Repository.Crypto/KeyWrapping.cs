using System.Security.Cryptography;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// Wraps and unwraps the key bundle under the KEK with AES-256-GCM — the only
/// wrap profile in format v1 (specification 03 §3). The framing bytes and AAD
/// assembly live in the format layer; this type sees only opaque nonce, AAD,
/// and payload spans.
/// </summary>
public static class KeyWrapping
{
    /// <summary>The wrap nonce is 12 bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>The wrap tag is 16 bytes.</summary>
    public const int TagLength = 16;

    /// <summary>
    /// Encrypts <paramref name="bundleCbor"/> into
    /// <paramref name="ciphertext"/> (same length) and <paramref name="tag"/>.
    /// </summary>
    public static void Wrap(
        Kek kek,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> bundleCbor,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        ArgumentNullException.ThrowIfNull(kek);

        using var aes = new AesGcm(kek.Bytes, TagLength);
        aes.Encrypt(nonce, bundleCbor, ciphertext, tag, aad);
    }

    /// <summary>
    /// Decrypts and authenticates a wrapped bundle, returning the CBOR
    /// plaintext.
    /// </summary>
    /// <exception cref="KeyUnwrapFailedException">
    /// Authentication failed — wrong passphrase or a tampered object,
    /// deliberately indistinguishable (specification 03 §3).
    /// </exception>
    public static byte[] Unwrap(
        Kek kek,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        ArgumentNullException.ThrowIfNull(kek);

        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(kek.Bytes, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new KeyUnwrapFailedException(exception);
        }

        return plaintext;
    }
}
