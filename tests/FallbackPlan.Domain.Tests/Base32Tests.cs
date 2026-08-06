using FsCheck.Xunit;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the lowercase unpadded base32 rendering (specification 00 §6)
/// against the RFC 4648 §10 test vectors, and its strict decoder against the
/// inputs it must refuse.
/// </summary>
public sealed class Base32Tests
{
    // RFC 4648 §10 vectors, lowercased and stripped of padding.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "my")]
    [InlineData("fo", "mzxq")]
    [InlineData("foo", "mzxw6")]
    [InlineData("foob", "mzxw6yq")]
    [InlineData("fooba", "mzxw6ytb")]
    [InlineData("foobar", "mzxw6ytboi")]
    public void Encode_matches_the_rfc_4648_vectors(string ascii, string expected)
    {
        Assert.Equal(expected, Base32.Encode(System.Text.Encoding.ASCII.GetBytes(ascii)));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("my", "f")]
    [InlineData("mzxq", "fo")]
    [InlineData("mzxw6", "foo")]
    [InlineData("mzxw6yq", "foob")]
    [InlineData("mzxw6ytb", "fooba")]
    [InlineData("mzxw6ytboi", "foobar")]
    public void Decode_matches_the_rfc_4648_vectors(string encoded, string expectedAscii)
    {
        var destination = new byte[16];

        Assert.True(Base32.TryDecode(encoded, destination, out var written));
        Assert.Equal(expectedAscii, System.Text.Encoding.ASCII.GetString(destination, 0, written));
    }

    [Theory]
    [InlineData("MY")]      // uppercase is not in the alphabet
    [InlineData("m y")]     // whitespace
    [InlineData("m1")]      // '1' is not in the alphabet
    [InlineData("m0")]      // '0' is not in the alphabet
    [InlineData("my=")]     // padding characters are not accepted
    public void Decode_rejects_characters_outside_the_alphabet(string encoded)
    {
        Assert.False(Base32.TryDecode(encoded, new byte[16], out _));
    }

    [Theory]
    [InlineData("m")]         // length % 8 == 1 cannot arise from whole bytes
    [InlineData("mzx")]       // length % 8 == 3
    [InlineData("mzxw6y")]    // length % 8 == 6
    public void Decode_rejects_impossible_lengths(string encoded)
    {
        Assert.False(Base32.TryDecode(encoded, new byte[16], out _));
    }

    [Fact]
    public void Decode_rejects_non_zero_trailing_padding_bits()
    {
        // "f" (0x66) encodes as "my": 'm'=12, 'y'=24 leaves three trailing
        // zero bits. "mz" ('z'=25) names the same byte with a non-zero
        // trailing bit; accepting it would break bijectivity.
        Assert.True(Base32.TryDecode("my", new byte[4], out _));
        Assert.False(Base32.TryDecode("mz", new byte[4], out _));
    }

    [Fact]
    public void Decode_rejects_a_destination_that_is_too_small()
    {
        Assert.False(Base32.TryDecode("mzxw6ytboi", new byte[3], out _));
    }

    [Property]
    public bool Decode_of_encode_returns_the_original_bytes(byte[]? bytes)
    {
        bytes ??= [];

        var encoded = Base32.Encode(bytes);
        var decoded = new byte[bytes.Length];

        return Base32.TryDecode(encoded, decoded, out var written)
            && written == bytes.Length
            && decoded.AsSpan(0, written).SequenceEqual(bytes);
    }
}
