using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the lowercase unpadded base32 rendering (specification 00 §6)
/// against the RFC 4648 §10 test vectors, and its strict decoder against the
/// inputs it must refuse.
/// </summary>
[TestClass]
public sealed class Base32Tests
{
    // RFC 4648 §10 vectors, lowercased and stripped of padding.
    [TestMethod]
    [DataRow("", "")]
    [DataRow("f", "my")]
    [DataRow("fo", "mzxq")]
    [DataRow("foo", "mzxw6")]
    [DataRow("foob", "mzxw6yq")]
    [DataRow("fooba", "mzxw6ytb")]
    [DataRow("foobar", "mzxw6ytboi")]
    public void Encode_matches_the_rfc_4648_vectors(string ascii, string expected)
    {
        SequenceAssert.AreEqual(expected, Base32.Encode(System.Text.Encoding.ASCII.GetBytes(ascii)));
    }

    [TestMethod]
    [DataRow("", "")]
    [DataRow("my", "f")]
    [DataRow("mzxq", "fo")]
    [DataRow("mzxw6", "foo")]
    [DataRow("mzxw6yq", "foob")]
    [DataRow("mzxw6ytb", "fooba")]
    [DataRow("mzxw6ytboi", "foobar")]
    public void Decode_matches_the_rfc_4648_vectors(string encoded, string expectedAscii)
    {
        var destination = new byte[16];

        Assert.IsTrue(Base32.TryDecode(encoded, destination, out var written));
        Assert.AreEqual(expectedAscii, System.Text.Encoding.ASCII.GetString(destination, 0, written));
    }

    [TestMethod]
    [DataRow("MY")]      // uppercase is not in the alphabet
    [DataRow("m y")]     // whitespace
    [DataRow("m1")]      // '1' is not in the alphabet
    [DataRow("m0")]      // '0' is not in the alphabet
    [DataRow("my=")]     // padding characters are not accepted
    public void Decode_rejects_characters_outside_the_alphabet(string encoded)
    {
        Assert.IsFalse(Base32.TryDecode(encoded, new byte[16], out _));
    }

    [TestMethod]
    [DataRow("m")]         // length % 8 == 1 cannot arise from whole bytes
    [DataRow("mzx")]       // length % 8 == 3
    [DataRow("mzxw6y")]    // length % 8 == 6
    public void Decode_rejects_impossible_lengths(string encoded)
    {
        Assert.IsFalse(Base32.TryDecode(encoded, new byte[16], out _));
    }

    [TestMethod]
    public void Decode_WhenTrailingPaddingBitsAreNonZero_ShouldThrow()
    {
        // "f" (0x66) encodes as "my": 'm'=12, 'y'=24 leaves three trailing
        // zero bits. "mz" ('z'=25) names the same byte with a non-zero
        // trailing bit; accepting it would break bijectivity.
        Assert.IsTrue(Base32.TryDecode("my", new byte[4], out _));
        Assert.IsFalse(Base32.TryDecode("mz", new byte[4], out _));
    }

    [TestMethod]
    public void Decode_WhenTheDestinationIsTooSmall_ShouldThrow()
    {
        Assert.IsFalse(Base32.TryDecode("mzxw6ytboi", new byte[3], out _));
    }

    [TestMethod]
    public void Decode_OfAnyEncodedInput_ReturnsTheOriginalBytes() =>
        PropertyCheck.Holds(this);

    public static bool Decode_OfAnyEncodedInput_ReturnsTheOriginalBytesProperty(byte[]? bytes)
    {
        bytes ??= [];

        var encoded = Base32.Encode(bytes);
        var decoded = new byte[bytes.Length];

        return Base32.TryDecode(encoded, decoded, out var written)
            && written == bytes.Length
            && decoded.AsSpan(0, written).SequenceEqual(bytes);
    }
}
