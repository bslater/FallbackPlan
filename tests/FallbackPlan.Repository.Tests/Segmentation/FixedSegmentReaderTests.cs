using FallbackPlan.Domain;
using FallbackPlan.Repository.Segmentation;

namespace FallbackPlan.Repository.Tests.Segmentation;

/// <summary>
/// Exercises the streaming fixed-v1 reader (specification 09 §2, §6):
/// boundary behaviour, short-read resilience, and the memory-model claim that
/// arithmetic at multi-tebibyte lengths costs nothing (FR-ARCH-013).
/// </summary>
public sealed class FixedSegmentReaderTests
{
    private static readonly SegmentSize SixtyFourKiB = SegmentSize.Create(64 * 1024);

    [Fact]
    public async Task An_empty_stream_yields_no_segments()
    {
        var reader = new FixedSegmentReader(new MemoryStream(), SixtyFourKiB);

        Assert.Null(await reader.ReadNextAsync(new byte[SixtyFourKiB.Bytes], CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_final_segment_may_be_short()
    {
        var reader = new FixedSegmentReader(new MemoryStream(new byte[150_000]), SixtyFourKiB);
        var buffer = new byte[SixtyFourKiB.Bytes];

        var segments = new List<SegmentDescriptor>();
        while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
        {
            segments.Add(segment);
        }

        Assert.Equal(3, segments.Count);
        Assert.Equal(SixtyFourKiB.Bytes, segments[0].Length);
        Assert.Equal(SixtyFourKiB.Bytes, segments[1].Length);
        Assert.Equal(150_000 - 2 * SixtyFourKiB.Bytes, segments[2].Length);
        Assert.Equal(2L * SixtyFourKiB.Bytes, segments[2].Offset);
    }

    [Fact]
    public async Task An_exact_multiple_has_no_short_segment()
    {
        var reader = new FixedSegmentReader(new MemoryStream(new byte[2 * SixtyFourKiB.Bytes]), SixtyFourKiB);
        var buffer = new byte[SixtyFourKiB.Bytes];

        var lengths = new List<int>();
        while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
        {
            lengths.Add(segment.Length);
        }

        Assert.Equal([SixtyFourKiB.Bytes, SixtyFourKiB.Bytes], lengths);
    }

    [Fact]
    public async Task Short_reads_from_the_stream_still_fill_whole_segments()
    {
        // A source that trickles one byte per read must not produce short
        // segments anywhere but the end (09 §2.1).
        var reader = new FixedSegmentReader(new OneByteAtATimeStream(new byte[70_000]), SixtyFourKiB);
        var buffer = new byte[SixtyFourKiB.Bytes];

        var first = await reader.ReadNextAsync(buffer, CancellationToken.None);
        var second = await reader.ReadNextAsync(buffer, CancellationToken.None);
        var third = await reader.ReadNextAsync(buffer, CancellationToken.None);

        Assert.Equal(SixtyFourKiB.Bytes, first!.Value.Length);
        Assert.Equal(70_000 - SixtyFourKiB.Bytes, second!.Value.Length);
        Assert.Null(third);
    }

    [Fact]
    public void Two_tebibyte_boundary_arithmetic_stays_exact_without_allocation()
    {
        const long TwoTiB = 2L * 1024 * 1024 * 1024 * 1024;

        Assert.Equal(TwoTiB / SixtyFourKiB.Bytes, FixedSegmentation.SegmentCount(TwoTiB, SixtyFourKiB));
        Assert.Equal(TwoTiB / SegmentSize.Create(64 * 1024 * 1024).Bytes, FixedSegmentation.SegmentCount(TwoTiB, SegmentSize.Create(64 * 1024 * 1024)));

        var (offset, length) = FixedSegmentation.GetSegment(TwoTiB / SixtyFourKiB.Bytes - 1, TwoTiB, SixtyFourKiB);
        Assert.Equal(TwoTiB - SixtyFourKiB.Bytes, offset);
        Assert.Equal(SixtyFourKiB.Bytes, length);

        Assert.Equal(TwoTiB / SixtyFourKiB.Bytes - 1, FixedSegmentation.SegmentIndexOfOffset(TwoTiB - 1, SixtyFourKiB));
    }

    [Fact]
    public void A_default_segment_size_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedSegmentation.SegmentCount(100, default));
        Assert.Throws<ArgumentException>(() => new FixedSegmentReader(new MemoryStream(), default));
    }

    [Fact]
    public async Task A_buffer_smaller_than_one_segment_is_refused()
    {
        var reader = new FixedSegmentReader(new MemoryStream(new byte[10]), SixtyFourKiB);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reader.ReadNextAsync(new byte[100], CancellationToken.None));
    }

    /// <summary>A stream that returns at most one byte per read.</summary>
    private sealed class OneByteAtATimeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
