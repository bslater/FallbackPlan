using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// Lowercase unpadded base32 (RFC 4648 §6) as required for identifiers that
/// appear in store keys (specification 00 §6, 02 §5). Store namespaces are
/// case-insensitive on some providers, which is why the rendering is base32
/// rather than hex or base64.
/// </summary>
/// <remarks>
/// <para>
/// The encoding itself is <c>Bodu.Text.Encoding</c>'s Base32 (standard
/// variant, padding omitted), adopted rather than hand-rolled because the
/// platform provides no base32 (ADR-0019 §4, ADR-0021). This adapter pins the
/// format's stricter contract on top: output is folded to lowercase, and
/// decoding refuses uppercase, out-of-alphabet characters, impossible
/// lengths, and non-zero trailing padding bits — Bodu enforces the trailing
/// bits via <c>RequireCanonicalEncoding</c>; the case and length rules are
/// this repository's own.
/// </para>
/// <para>
/// The strictness exists for bijectivity: exactly one rendering names an
/// identifier, so two distinct strings can never decode to the same bytes.
/// </para>
/// </remarks>
public static class Base32
{
    private const Bodu.Text.Encoding.BaseFormatStyles StrictUnpadded =
        Bodu.Text.Encoding.BaseFormatStyles.AllowMissingPadding |
        Bodu.Text.Encoding.BaseFormatStyles.RequireCanonicalEncoding;

    /// <summary>
    /// Returns the number of characters produced by encoding
    /// <paramref name="byteCount"/> bytes.
    /// </summary>
    public static int GetEncodedLength(int byteCount)
    {
        ThrowHelper.ThrowIfNegative(byteCount);
        return checked((byteCount * 8 + 4) / 5);
    }

    /// <summary>
    /// Encodes <paramref name="bytes"/> as lowercase unpadded base32.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var encoded = Bodu.Text.Encoding.Base32.Encode(
            bytes,
            Bodu.Text.Encoding.Base32Variant.Standard,
            Bodu.Text.Encoding.BaseFormattingOptions.OmitPadding);

        // The standard alphabet is uppercase; the store rendering is lowercase
        // (specification 00 §6).
        return encoded.ToLowerInvariant();
    }

    /// <summary>
    /// Decodes lowercase unpadded base32 into <paramref name="destination"/>.
    /// Returns <see langword="false"/> for uppercase or out-of-alphabet
    /// characters, an impossible length, non-zero trailing padding bits, or a
    /// destination that is too small.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        // Unpadded base32 lengths are 8k + {0, 2, 4, 5, 7}; the remainders
        // 1, 3, and 6 cannot arise from any whole number of input bytes.
        var remainder = text.Length % 8;
        if (remainder is 1 or 3 or 6)
        {
            return false;
        }

        // Only the exact rendering alphabet is accepted. The underlying
        // decoder is case-insensitive and accepts '=' padding; the format's
        // renderings are strictly lowercase and unpadded, and bijectivity
        // demands refusing every other spelling.
        foreach (var character in text)
        {
            var valid = character is (>= 'a' and <= 'z') or (>= '2' and <= '7');
            if (!valid)
            {
                return false;
            }
        }

        return Bodu.Text.Encoding.Base32.TryDecode(
            text,
            destination,
            out bytesWritten,
            Bodu.Text.Encoding.Base32Variant.Standard,
            StrictUnpadded);
    }
}
