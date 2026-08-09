using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the zstd segment codec (specification 10 §4): single standard
/// frame, threshold-gated storage, and the reader-side limits that make a
/// compression bomb a refusal instead of an allocation.
/// </summary>
[TestClass]
public sealed class ZstdSegmentCodecTests
{
    private static readonly SegmentSize SixtyFourKiB = SegmentSize.Create(64 * 1024);

    private static ZstdSegmentCodec CreateCodec() => new(level: 3, SixtyFourKiB);

    private static byte[] Compressible(int length) =>
        [.. Enumerable.Repeat("compressible segment content "u8.ToArray(), length / 29 + 1).SelectMany(text => text).Take(length)];

    [TestMethod]
    public void ZstdSegmentCodec_CompressedThenDecompressed_RoundTripsByteIdentically()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(50_000);
        var frame = new byte[plaintext.Length];

        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        Assert.IsTrue(written < plaintext.Length);

        var restored = new byte[plaintext.Length];
        codec.Decompress(frame.AsSpan(0, written), restored);

        SequenceAssert.AreEqual(plaintext, restored);
    }

    [TestMethod]
    public void ZstdSegmentCodec_AnyOutput_IsASingleStandardZstdFrame()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length];

        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        // RFC 8878 frame magic, little-endian 0xFD2FB528 (specification 10 §4.1).
        SequenceAssert.AreEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, frame[..4]);
    }

    [TestMethod]
    public void ZstdSegmentCodec_InputIsIncompressible_StoresItUncompressed()
    {
        using var codec = CreateCodec();
        var plaintext = new byte[10_000];
        RandomNumberGenerator.Fill(plaintext);
        var frame = new byte[plaintext.Length];

        Assert.IsFalse(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        Assert.AreEqual(0, written);
    }

    [TestMethod]
    public void Decompress_WhenTheFrameExceedsTheLogicalLength_ShouldThrow()
    {
        // A record's logical_length is the hard output limit (10 §4.1): a
        // frame claiming more is refused, never allocated for.
        using var codec = CreateCodec();
        var plaintext = Compressible(20_000);
        var frame = new byte[plaintext.Length];
        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        Assert.ThrowsExactly<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written), new byte[10_000]));
    }

    [TestMethod]
    public void Decompress_WhenTheResultIsShorterThanDeclared_ShouldThrow()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length];
        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        Assert.ThrowsExactly<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written), new byte[20_000]));
    }

    [TestMethod]
    public void Decompress_WhenBytesTrailTheFrame_ShouldThrow()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length + 4];
        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        frame[written] = 0xAA; // trailing garbage — multi-frame and skippable frames are forbidden

        Assert.ThrowsExactly<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written + 1), new byte[plaintext.Length]));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(20)]
    [DataRow(-1)]
    public void ZstdSegmentCodec_CompressionLevelIsOutOfRange_IsRefused(int level)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ZstdSegmentCodec(level, SixtyFourKiB));
    }

    [TestMethod]
    public void ZstdSegmentCodec_TheStorageThreshold_IsAPermilleValue()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CompressionThreshold.StoreCompressed(1000, 1, 1001));
    }
}
