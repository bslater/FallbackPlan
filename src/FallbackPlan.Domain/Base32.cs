namespace FallbackPlan.Domain;

/// <summary>
/// Lowercase unpadded base32 (RFC 4648 §6, alphabet
/// <c>abcdefghijklmnopqrstuvwxyz234567</c>) as required for identifiers that
/// appear in store keys (specification 00 §6, 02 §5). Store namespaces are
/// case-insensitive on some providers, which is why the rendering is base32
/// rather than hex or base64.
/// </summary>
/// <remarks>
/// Decoding is strict: uppercase characters, characters outside the alphabet,
/// impossible lengths, and non-zero trailing padding bits are all rejected.
/// RFC 4648 §3.5 merely recommends rejecting non-zero padding bits; this
/// implementation requires it so that encoding and decoding form a bijection —
/// two distinct renderings can never name the same identifier.
/// </remarks>
public static class Base32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// Returns the number of characters produced by encoding
    /// <paramref name="byteCount"/> bytes.
    /// </summary>
    public static int GetEncodedLength(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
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

        var result = new char[GetEncodedLength(bytes.Length)];
        var buffer = 0;
        var bits = 0;
        var written = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                result[written++] = Alphabet[(buffer >> bits) & 0x1F];
            }
        }

        if (bits > 0)
        {
            result[written++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        }

        return new string(result, 0, written);
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

        var buffer = 0;
        var bits = 0;
        var written = 0;

        foreach (var character in text)
        {
            int index;
            if (character is >= 'a' and <= 'z')
            {
                index = character - 'a';
            }
            else if (character is >= '2' and <= '7')
            {
                index = character - '2' + 26;
            }
            else
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;

                if (written >= destination.Length)
                {
                    return false;
                }

                destination[written++] = (byte)((buffer >> bits) & 0xFF);
            }
        }

        // Bits left over after the final whole byte are padding and must be
        // zero, or two distinct strings would decode to the same bytes.
        if ((buffer & ((1 << bits) - 1)) != 0)
        {
            return false;
        }

        bytesWritten = written;
        return true;
    }
}
