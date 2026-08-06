using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the 88-byte blob envelope byte-exactly (specification 05 §2).
/// </summary>
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

    [Fact]
    public void Field_offsets_are_byte_exact()
    {
        var bytes = SampleBytes();

        Assert.Equal("FBPKBLOB"u8.ToArray(), bytes[..8]);
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[8..10]);   // format_version
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[10..12]);  // blob_class data
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03 }, bytes[12..16]); // generation
        Assert.Equal(0xA0, bytes[16]);                            // blob_id
        Assert.Equal(0x5A, bytes[32]);                            // blob_salt
        Assert.Equal(0x2A, bytes[71]);                            // blob_counter 42 BE
        Assert.Equal(0xA0, bytes[72]);                            // writer_id
        Assert.Equal(0xAF, bytes[87]);
    }

    [Fact]
    public void Parse_round_trips()
    {
        var parsed = BlobEnvelope.Parse(SampleBytes());

        Assert.Equal(BlobClass.Data, parsed.BlobClass);
        Assert.Equal(3u, parsed.KeyGeneration.Value);
        Assert.Equal(42UL, parsed.BlobCounter);
        Assert.True(parsed.BlobSalt.SequenceEqual(Enumerable.Repeat((byte)0x5A, 32).ToArray()));
    }

    [Fact]
    public void An_absent_magic_says_not_a_blob()
    {
        var bytes = SampleBytes();
        bytes[0] = (byte)'X';

        var exception = Assert.Throws<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
        Assert.Contains("Not a FallbackPlan blob", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undefined_class_is_refused()
    {
        var bytes = SampleBytes();
        bytes[11] = 0x03;

        Assert.Throws<BlobFormatException>(() => BlobEnvelope.Parse(bytes));
    }
}
