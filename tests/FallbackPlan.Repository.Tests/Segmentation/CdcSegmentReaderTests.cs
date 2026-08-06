using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Segmentation;

namespace FallbackPlan.Repository.Tests.Segmentation;

/// <summary>
/// The cdc-v1 streaming reader (specification 09 §3; ADR-0023; FR-ARCH-014):
/// boundaries obey min/max, cover the input contiguously, are independent of
/// how the stream doles out bytes, and resynchronise after an insertion.
/// </summary>
public sealed class CdcSegmentReaderTests
{
    private static readonly CdcParameters Parameters =
        CdcParameters.Create(64 * 1024, 8 * 1024, 512 * 1024);

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static async Task<List<SegmentDescriptor>> SegmentAsync(Stream source, CdcParameters parameters)
    {
        var reader = new CdcSegmentReader(source, parameters);
        var buffer = new byte[parameters.MaxSize];
        var segments = new List<SegmentDescriptor>();

        while (await reader.ReadNextAsync(buffer, CancellationToken.None) is { } segment)
        {
            segments.Add(segment);
        }

        return segments;
    }

    [Fact]
    public async Task Segments_are_contiguous_cover_the_input_and_obey_the_bounds()
    {
        var data = RandomBytes(3_000_000, seed: 42);

        using var source = new MemoryStream(data);
        var segments = await SegmentAsync(source, Parameters);

        var offset = 0L;
        foreach (var segment in segments)
        {
            Assert.Equal(offset, segment.Offset);
            Assert.InRange(segment.Length, 1, Parameters.MaxSize);
            offset += segment.Length;
        }

        Assert.Equal(data.Length, offset);

        // Every segment but the last respects min_size (09 §3.1); the final
        // short segment is emitted as-is.
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            Assert.True(segment.Length >= Parameters.MinSize);
        }
    }

    [Fact]
    public async Task Boundaries_do_not_depend_on_how_the_stream_doles_out_bytes()
    {
        var data = RandomBytes(1_200_000, seed: 7);

        using var oneShot = new MemoryStream(data);
        var expected = await SegmentAsync(oneShot, Parameters);

        using var trickle = new TrickleStream(data, chunkSize: 4093);
        var actual = await SegmentAsync(trickle, Parameters);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task A_one_byte_insertion_realigns_every_downstream_boundary()
    {
        var data = RandomBytes(2_000_000, seed: 11);
        var inserted = new byte[data.Length + 1];
        inserted[0] = 0x5A;
        data.CopyTo(inserted, 1);

        using var original = new MemoryStream(data);
        using var shifted = new MemoryStream(inserted);
        var baseCuts = (await SegmentAsync(original, Parameters))
            .Select(s => s.Offset + s.Length).ToArray();
        var shiftedCuts = (await SegmentAsync(shifted, Parameters))
            .Select(s => s.Offset + s.Length - 1).ToArray();

        // Boundaries are a pure function of the local 64-byte window
        // (09 §3.2), so a front insertion shifts every cut by exactly one.
        Assert.Equal(baseCuts[..^1], shiftedCuts[..^1]);
    }

    [Fact]
    public async Task An_all_zero_input_cuts_at_exactly_min_size()
    {
        using var source = new MemoryStream(new byte[100_000]);
        var segments = await SegmentAsync(source, Parameters);

        // The zero window fingerprints to zero, so every boundary test
        // passes the moment min_size is reached.
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            Assert.Equal(Parameters.MinSize, segment.Length);
        }
    }

    [Fact]
    public async Task An_empty_source_yields_no_segments()
    {
        using var source = new MemoryStream([]);

        Assert.Empty(await SegmentAsync(source, Parameters));
    }

    [Fact]
    public async Task The_rolling_state_matches_the_direct_window_reduction()
    {
        // Hash 200 bytes through Advance and compare position 63 and 199
        // against the definitional form: the 64-byte window as a big-endian
        // GF(2) polynomial reduced mod P (ADR-0023).
        var data = SHA256.HashData("cdc-window-probe"u8);
        var stretched = new byte[200];
        for (var i = 0; i < stretched.Length; i++)
        {
            stretched[i] = data[i % data.Length];
        }

        var state = 0UL;
        var window = new byte[RabinFingerprint.WindowSize];

        for (var p = 0; p < stretched.Length; p++)
        {
            var slot = p & (RabinFingerprint.WindowSize - 1);
            var outgoing = window[slot];
            window[slot] = stretched[p];
            state = RabinFingerprint.Advance(state, stretched[p], outgoing);

            if (p is 63 or 199)
            {
                Assert.Equal(DirectReduction(stretched.AsSpan(p - 63, 64)), state);
            }
        }
    }

    [Fact]
    public async Task A_buffer_smaller_than_the_maximum_segment_is_refused()
    {
        using var source = new MemoryStream(new byte[16]);
        var reader = new CdcSegmentReader(source, Parameters);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reader.ReadNextAsync(new byte[Parameters.MaxSize - 1], CancellationToken.None));
    }

    [Fact]
    public void Unset_parameters_are_refused_at_construction()
    {
        using var source = new MemoryStream([]);

        Assert.Throws<ArgumentException>(() => new CdcSegmentReader(source, default));
    }

    /// <summary>Definitional reduction: 64 window bytes, MSB-first, mod P(x) = x⁶⁴ + x⁴ + x³ + x + 1.</summary>
    private static ulong DirectReduction(ReadOnlySpan<byte> window)
    {
        var state = 0UL;

        foreach (var value in window)
        {
            // state·x⁸ mod P, one bit at a time, then absorb the byte.
            for (var bit = 0; bit < 8; bit++)
            {
                var overflow = (state & 0x8000_0000_0000_0000UL) != 0;
                state <<= 1;
                if (overflow)
                {
                    state ^= RabinFingerprint.PolynomialLow;
                }
            }

            state ^= value;
        }

        return state;
    }

    /// <summary>A stream that returns at most <c>chunkSize</c> bytes per read.</summary>
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
            data.AsSpan(_position, take).CopyTo(buffer.AsSpan(offset));
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
