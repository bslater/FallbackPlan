using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// Streams a file as <c>fixed-v1</c> segments (specification 09 §2, §6;
/// FR-ARCH-001, FR-ARCH-013): the caller supplies one segment-sized buffer,
/// the reader fills it per segment, and memory stays bounded by the segment
/// size — never by file length, file count, or repository size
/// (NFR-PERF-001).
/// </summary>
/// <remarks>
/// The source needs no declared length: the reader streams to end of stream,
/// so an unseekable pipe segments the same way a file does (09 §6 step 1).
/// Only the final segment may be short; an empty source yields no segments —
/// a zero-length file has an empty segment list by specification, not a
/// zero-length segment (09 §2.1).
/// </remarks>
public sealed class FixedSegmentReader
{
    private readonly Stream _source;
    private readonly SegmentSize _segmentSize;
    private long _nextIndex;
    private long _nextOffset;
    private bool _endOfStream;

    /// <summary>Creates a reader over <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentException">The source is not readable or the segment size unset.</exception>
    public FixedSegmentReader(Stream source, SegmentSize segmentSize)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (segmentSize.Bytes == 0)
        {
            throw new ArgumentException(
                "The segment size is unset; construct it via SegmentSize.Create (specification 09 §2.2).",
                nameof(segmentSize));
        }

        _source = source;
        _segmentSize = segmentSize;
    }

    /// <summary>
    /// Reads the next segment into <paramref name="buffer"/>, looping over
    /// short reads until the segment is complete or the stream ends. Returns
    /// <see langword="null"/> when the source is exhausted.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is smaller than one segment.</exception>
    public async ValueTask<SegmentDescriptor?> ReadNextAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length < _segmentSize.Bytes)
        {
            throw new ArgumentException(
                $"The buffer must hold at least one segment ({_segmentSize.Bytes} bytes); got {buffer.Length}.",
                nameof(buffer));
        }

        if (_endOfStream)
        {
            return null;
        }

        var target = buffer[.._segmentSize.Bytes];
        var filled = 0;

        while (filled < target.Length)
        {
            var read = await _source.ReadAsync(target[filled..], cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                _endOfStream = true;
                break;
            }

            filled += read;
        }

        if (filled == 0)
        {
            return null;
        }

        var descriptor = new SegmentDescriptor(_nextIndex, _nextOffset, filled);
        _nextIndex++;
        _nextOffset += filled;

        return descriptor;
    }
}
