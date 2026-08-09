using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// Limits are enforced before allocating (specification 00 §8): a declared
/// length above the caller's maximum is refused from the length prefix alone,
/// never by allocating first and measuring afterwards.
/// </summary>
[TestClass]
public sealed class CanonicalCborLimitsTests
{
    [TestMethod]
    public void ReadByteString_WhenLongerThanTheStatedMaximum_ShouldThrow()
    {
        // 100-byte string, canonical prefix 58 64.
        var bytes = new byte[102];
        bytes[0] = 0x58;
        bytes[1] = 0x64;

        var reader = new CanonicalCborReader(bytes);

        Assert.ThrowsExactly<CborFormatException>(() => reader.ReadByteString(maxLength: 10));
    }

    [TestMethod]
    public void ReadByteString_WhenWithinTheStatedMaximum_ShouldReturnIt()
    {
        var bytes = Convert.FromHexString("43010203");

        var reader = new CanonicalCborReader(bytes);

        SequenceAssert.AreEqual<byte>([1, 2, 3], reader.ReadByteString(maxLength: 10));
    }

    [TestMethod]
    public void ReadTextString_WhenLongerThanTheStatedMaximum_ShouldThrow()
    {
        // 100-character text string, canonical prefix 78 64.
        var bytes = new byte[102];
        bytes[0] = 0x78;
        bytes[1] = 0x64;
        Array.Fill(bytes, (byte)'a', 2, 100);

        var reader = new CanonicalCborReader(bytes);

        Assert.ThrowsExactly<CborFormatException>(() => reader.ReadTextString(maxUtf8Length: 10));
    }

    [TestMethod]
    public void ReadArray_WhenLongerThanTheStatedMaximum_ShouldThrow()
    {
        // Five-element array of zeros.
        var bytes = Convert.FromHexString("850000000000");

        var reader = new CanonicalCborReader(bytes);

        Assert.ThrowsExactly<CborFormatException>(() => reader.ReadStartArray(maxCount: 4));
    }

    [TestMethod]
    public void ReadFixedLengthByteString_WhenTheLengthDiffers_ShouldThrow()
    {
        var reader = new CanonicalCborReader(Convert.FromHexString("43010203"));

        Assert.ThrowsExactly<CborFormatException>(() => reader.ReadFixedByteString(expectedLength: 16));
    }
}
