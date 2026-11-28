using System.Security.Cryptography;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The synthetic input the memory measurements archive. Content that
/// repeated would be measured as deduplication rather than processing: a
/// repeated segment is hashed, found, and never compressed, encrypted,
/// packed or uploaded, so a pipeline that leaked on any of those paths
/// would look bounded. This is the instrument, and does not establish
/// FR-ARCH-002 itself.
/// </summary>
[TestClass]
public sealed class SyntheticStreamTests
{
    private const int Mebibyte = 1024 * 1024;

    [TestMethod]
    public void BeyondItsFirst256MiB_TheStreamDoesNotRepeatWhatItAlreadyGave()
    {
        using var stream = new SyntheticStream(257L * Mebibyte, seed: 42);
        var first = ReadMebibyte(stream);
        SkipMebibytes(stream, 255);
        var after = ReadMebibyte(stream);

        Assert.IsFalse(first.AsSpan().SequenceEqual(after), "the 257th mebibyte repeats the first");
    }

    [TestMethod]
    public void ItsFirst256MiB_AreTheBytesTheSmallerSuitesAlreadyArchive()
    {
        // Every suite that archives this stream takes less than 256 MiB of
        // it; their sizes and boundaries were measured on these bytes.
        using var stream = new SyntheticStream(256L * Mebibyte, seed: 42);
        var first = ReadMebibyte(stream);
        SkipMebibytes(stream, 254);
        var last = ReadMebibyte(stream);

        Assert.AreEqual("68363F11DBA58B158E9EA2EF283E59CCC6E1F6788D347BE36EA814E7E315F3E4", Convert.ToHexString(SHA256.HashData(first)));
        Assert.AreEqual("667AC64B672F614485303C59EE32D33D30103595690C77E825D31F623C1FEE6B", Convert.ToHexString(SHA256.HashData(last)));
    }

    private static byte[] ReadMebibyte(Stream stream)
    {
        var buffer = new byte[Mebibyte];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void SkipMebibytes(Stream stream, int count)
    {
        var buffer = new byte[Mebibyte];
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(buffer);
        }
    }
}
