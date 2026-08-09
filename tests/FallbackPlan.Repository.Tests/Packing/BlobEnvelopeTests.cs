using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the 88-byte blob envelope byte-exactly (specification 05 §2).
/// </summary>
[TestClass]
public sealed class BlobEnvelopeTests
{
    private static BlobEnvelope Sample() => new(
        FormatLimits.FormatVersion,
        BlobClass.Data,
        new KeyGeneration(3),
        BlobId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7000000000000002a")),
        Enumerable.Repeat((byte)0x5A, 32).ToArray(),
        42,
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf")));

    private static byte[] SampleBytes()
    {
        var bytes = new byte[BlobEnvelope.Length];
        Sample().WriteTo(bytes);
        return bytes;
    }

    [TestMethod]
    public void Field_offsets_are_byte_exact()
    {
        var bytes = SampleBytes();

        SequenceAssert.AreEqual("FBPKBLOB"u8.ToArray(), bytes[..8]);
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[8..10]);   // format_version
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[10..12]);  // blob_class data
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x03 }, bytes[12..16]); // generation
        Assert.AreEqual(0xA0, bytes[16]);                            // blob_id
        Assert.AreEqual(0x5A, bytes[32]);                            // blob_salt
        Assert.AreEqual(0x2A, bytes[71]);                            // blob_counter 42 BE
        Assert.AreEqual(0xA0, bytes[72]);                            // writer_id
        Assert.AreEqual(0xAF, bytes[87]);
    }

    [TestMethod]
    public void Parse_round_trips()
    {
        var parsed = BlobEnvelope.Parse(SampleBytes());

        Assert.AreEqual(BlobClass.Data, parsed.BlobClass);
        Assert.AreEqual(3u, parsed.KeyGeneration.Value);
        Assert.AreEqual(42UL, parsed.BlobCounter);
        Assert.IsTrue(parsed.BlobSalt.SequenceEqual(Enumerable.Repeat((byte)0x5A, 32).ToArray()));
    }

    [TestMethod]
    public void An_absent_magic_says_not_a_blob()
    {
        var bytes = SampleBytes();
        bytes[0] = (byte)'X';

        var exception = Assert.ThrowsExactly<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
        Assert.Contains("Not a FallbackPlan blob", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void An_undefined_class_is_refused()
    {
        var bytes = SampleBytes();
        bytes[11] = 0x03;

        Assert.ThrowsExactly<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
    }
}
