using FallbackPlan.Domain;
using FallbackPlan.Repository.Segmentation;
using FsCheck.Xunit;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>
/// cdc-v1 as properties over generated inputs (ADR-0023; specification
/// 09 §3; FR-ARCH-014): for any input, segments are contiguous, cover the
/// input exactly, obey the min/max bounds, and are a pure function of the
/// content — never of how the stream doles bytes out.
/// </summary>
public sealed class CdcPropertyTests
{
    private static readonly CdcParameters Parameters =
        CdcParameters.Create(64 * 1024, 8 * 1024, 512 * 1024);

    /// <summary>Expands a small generated seed into a deterministic buffer big enough to cut.</summary>
    private static byte[] Expand(int seed, byte sizeSeed)
    {
        var length = 1 + (sizeSeed * 3001);
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static async Task<List<SegmentDescriptor>> SegmentAsync(Stream source)
    {
        var reader = new CdcSegmentReader(source, Parameters);
        var buffer = new byte[reader.RequiredBufferSize];
        var segments = new List<SegmentDescriptor>();

        while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
        {
            segments.Add(segment);
        }

        return segments;
    }

    [Property(MaxTest = 50)]
    public async Task Segments_are_contiguous_cover_the_input_and_obey_the_bounds(int seed, byte sizeSeed)
    {
        var data = Expand(seed, sizeSeed);

        using var source = new MemoryStream(data);
        var segments = await SegmentAsync(source);

        var offset = 0L;
        var index = 0L;
        foreach (var segment in segments)
        {
            Assert.Equal(index, segment.Index);
            Assert.Equal(offset, segment.Offset);
            Assert.InRange(segment.Length, 1, Parameters.MaxSize);
            offset += segment.Length;
            index++;
        }

        Assert.Equal(data.Length, offset);

        // Every segment but the last respects min_size (09 §3.1); only the
        // final short segment may fall under it.
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            Assert.True(segment.Length >= Parameters.MinSize,
                $"segment {segment.Index} is {segment.Length} bytes, under min {Parameters.MinSize}");
        }
    }

    [Property(MaxTest = 30)]
    public async Task Boundaries_are_deterministic_and_independent_of_stream_chunking(
        int seed, byte sizeSeed, ushort chunkSeed)
    {
        var data = Expand(seed, sizeSeed);

        using var oneShot = new MemoryStream(data);
        var expected = await SegmentAsync(oneShot);

        using var again = new MemoryStream(data);
        Assert.Equal(expected, await SegmentAsync(again));

        using var trickle = new TrickleStream(data, chunkSize: 1 + chunkSeed % 8192);
        Assert.Equal(expected, await SegmentAsync(trickle));
    }

    /// <summary>A stream that returns at most <paramref name="chunkSize"/> bytes per read.</summary>
    private sealed class TrickleStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(chunkSize, Math.Min(count, data.Length - _position));
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
