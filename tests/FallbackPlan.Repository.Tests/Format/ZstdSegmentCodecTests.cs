using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Compression;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the zstd segment codec (specification 10 §4): single standard
/// frame, threshold-gated storage, and the reader-side limits that make a
/// compression bomb a refusal instead of an allocation.
/// </summary>
public sealed class ZstdSegmentCodecTests
{
    private static readonly SegmentSize SixtyFourKiB = SegmentSize.Create(64 * 1024);

    private static ZstdSegmentCodec CreateCodec() => new(level: 3, SixtyFourKiB);

    private static byte[] Compressible(int length) =>
        [.. Enumerable.Repeat("compressible segment content "u8.ToArray(), length / 29 + 1).SelectMany(text => text).Take(length)];

    [Fact]
    public void Round_trip_is_byte_identical()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(50_000);
        var frame = new byte[plaintext.Length];

        Assert.True(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        Assert.True(written < plaintext.Length);

        var restored = new byte[plaintext.Length];
        codec.Decompress(frame.AsSpan(0, written), restored);

        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void Output_is_a_single_standard_zstd_frame()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length];

        Assert.True(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        // RFC 8878 frame magic, little-endian 0xFD2FB528 (specification 10 §4.1).
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, frame[..4]);
    }

    [Fact]
    public void Incompressible_input_is_stored_uncompressed()
    {
        using var codec = CreateCodec();
        var plaintext = new byte[10_000];
        RandomNumberGenerator.Fill(plaintext);
        var frame = new byte[plaintext.Length];

        Assert.False(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Decompress_refuses_a_frame_exceeding_the_logical_length()
    {
        // A record's logical_length is the hard output limit (10 §4.1): a
        // frame claiming more is refused, never allocated for.
        using var codec = CreateCodec();
        var plaintext = Compressible(20_000);
        var frame = new byte[plaintext.Length];
        Assert.True(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        Assert.Throws<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written), new byte[10_000]));
    }

    [Fact]
    public void Decompress_refuses_a_short_result()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length];
        Assert.True(codec.TryCompressForStorage(plaintext, 50, frame, out var written));

        Assert.Throws<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written), new byte[20_000]));
    }

    [Fact]
    public void Decompress_refuses_trailing_bytes_after_the_frame()
    {
        using var codec = CreateCodec();
        var plaintext = Compressible(10_000);
        var frame = new byte[plaintext.Length + 4];
        Assert.True(codec.TryCompressForStorage(plaintext, 50, frame, out var written));
        frame[written] = 0xAA; // trailing garbage — multi-frame and skippable frames are forbidden

        Assert.Throws<CompressionFormatException>(() =>
            codec.Decompress(frame.AsSpan(0, written + 1), new byte[plaintext.Length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(-1)]
    public void Levels_outside_one_to_nineteen_are_refused(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdSegmentCodec(level, SixtyFourKiB));
    }

    [Fact]
    public void The_threshold_is_a_permille_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompressionThreshold.StoreCompressed(1000, 1, 1001));
    }
}
