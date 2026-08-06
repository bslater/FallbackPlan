using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// Streams a file as <c>cdc-v1</c> segments (specification 09 §3;
/// FR-ARCH-014): a boundary falls at position <em>p</em> when the Rabin
/// fingerprint of the 64 bytes ending at <em>p</em> satisfies
/// <c>(H &amp; mask) == 0</c> and the segment has reached <c>min_size</c>, or
/// unconditionally at <c>max_size</c>. Memory stays bounded by the maximum
/// segment size (NFR-PERF-001).
/// </summary>
/// <remarks>
/// The window and state persist across segment boundaries — never reset — so
/// boundaries are a pure function of local content, which is what makes an
/// insertion perturb only its neighbourhood (09 §3.2). The final segment is
/// emitted as-is, however short; an empty source yields no segments
/// (09 §2.1, applied to cdc by ADR-0023).
/// </remarks>
public sealed class CdcSegmentReader : ISegmentReader
{
    private readonly Stream _source;
    private readonly CdcParameters _parameters;
    private readonly ulong _mask;
    private readonly byte[] _window = new byte[RabinFingerprint.WindowSize];
    private readonly byte[] _carry;
    private int _carryLength;
    private ulong _state;
    private long _windowPosition;
    private long _nextIndex;
    private long _nextOffset;
    private bool _endOfStream;

    /// <summary>Creates a reader over <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentException">The source is not readable or the parameters unset.</exception>
    public CdcSegmentReader(Stream source, CdcParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (parameters.TargetSize == 0)
        {
            throw new ArgumentException(
                "The cdc parameters are unset; construct them via CdcParameters.Create (specification 09 §3.1).",
                nameof(parameters));
        }

        _source = source;
        _parameters = parameters;
        _mask = parameters.Mask;
        _carry = new byte[parameters.MaxSize];
    }

    /// <inheritdoc />
    public int RequiredBufferSize => _parameters.MaxSize;

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is smaller than the maximum segment.</exception>
    public async ValueTask<SegmentDescriptor?> ReadNextAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length < _parameters.MaxSize)
        {
            throw new ArgumentException(
                $"The buffer must hold the maximum segment size ({_parameters.MaxSize} bytes); got {buffer.Length}.",
                nameof(buffer));
        }

        // Bytes carried over from the previous call were never hashed — the
        // scan stopped at the boundary — so they re-enter here verbatim.
        _carry.AsSpan(0, _carryLength).CopyTo(buffer.Span);
        var filled = _carryLength;
        _carryLength = 0;

        while (!_endOfStream && filled < _parameters.MaxSize)
        {
            var read = await _source.ReadAsync(buffer[filled.._parameters.MaxSize], cancellationToken)
                .ConfigureAwait(false);

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

        // A full buffer always cuts (length == max_size is a forced
        // boundary); a partial buffer at end of stream may not — the
        // remainder is the final short segment.
        var cut = ScanForBoundary(buffer.Span[..filled]);
        var segmentLength = cut >= 0 ? cut : filled;

        var remainder = filled - segmentLength;
        if (remainder > 0)
        {
            buffer.Span.Slice(segmentLength, remainder).CopyTo(_carry);
            _carryLength = remainder;
        }

        var descriptor = new SegmentDescriptor(_nextIndex, _nextOffset, segmentLength);
        _nextIndex++;
        _nextOffset += segmentLength;

        return descriptor;
    }

    /// <summary>
    /// Hashes bytes in order until a boundary is declared, returning the
    /// segment length, or −1 with every byte hashed when no boundary falls
    /// within <paramref name="bytes"/>.
    /// </summary>
    private int ScanForBoundary(ReadOnlySpan<byte> bytes)
    {
        var state = _state;
        var position = _windowPosition;
        var window = _window;

        for (var i = 0; i < bytes.Length; i++)
        {
            var slot = (int)(position & (RabinFingerprint.WindowSize - 1));
            var outgoing = window[slot];
            var incoming = bytes[i];
            window[slot] = incoming;
            position++;

            state = RabinFingerprint.Advance(state, incoming, outgoing);

            var length = i + 1;
            if (length == _parameters.MaxSize || (length >= _parameters.MinSize && (state & _mask) == 0))
            {
                _state = state;
                _windowPosition = position;
                return length;
            }
        }

        _state = state;
        _windowPosition = position;
        return -1;
    }
}
