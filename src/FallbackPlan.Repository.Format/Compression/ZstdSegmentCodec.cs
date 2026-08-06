using System.Buffers;
using System.Numerics;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using ZstdSharp;

namespace FallbackPlan.Repository.Format.Compression;

/// <summary>
/// The <c>zstd-v1</c> codec for segment records (specification 10 §4;
/// FR-ARCH-005): one standard Zstandard frame per record, no skippable
/// frames, no concatenation, no dictionaries.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place the zstd library appears. The writer's window log
/// is bounded to the segment size — <c>min(level default, log2(segment
/// size))</c>; the specification pins no exact value because compressed
/// bytes are deliberately not conformance-pinned (10 §7), so the choice is
/// recorded here rather than in a vector. The decompressor's window is
/// capped at 2<sup>26</sup> (the 64 MiB record limit) so a hostile frame
/// header cannot demand unbounded decoder memory.
/// </para>
/// <para>
/// Decompression enforces the record's <c>logical_length</c> as an exact
/// output size: a frame producing more (the library refuses the
/// over-long output), less, or carrying trailing input bytes is a
/// <see cref="CompressionFormatException"/> (10 §4.1). Instances wrap
/// stateful compressor/decompressor contexts and are not thread-safe — one
/// codec per pipeline.
/// </para>
/// </remarks>
public sealed class ZstdSegmentCodec : IDisposable
{
    /// <summary>
    /// The codec identity the spool checkpoint pins (specification 05 §6.2):
    /// the loaded compression library and its exact version. Zstandard output
    /// is deterministic only within one library version, so a resume under a
    /// different value MUST restart, never recompress.
    /// </summary>
    public static readonly string CodecVersion =
        $"ZstdSharp.Port {typeof(Compressor).Assembly.GetName().Version}";

    private readonly Compressor _compressor;
    private readonly ZstdSegmentDecompressor _decompressor;

    /// <summary>
    /// Creates a codec at <paramref name="level"/> for segments of
    /// <paramref name="segmentSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1–19 (specification 10 §4).</exception>
    public ZstdSegmentCodec(int level, SegmentSize segmentSize)
        : this(level, segmentSize.Bytes)
    {
    }

    /// <summary>
    /// Creates a codec at <paramref name="level"/> for segments of at most
    /// <paramref name="maximumSegmentBytes"/> — the shape <c>cdc-v1</c>
    /// needs, whose maximum segment size need not be a power of two.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1–19 (specification 10 §4).</exception>
    /// <exception cref="ArgumentException">The maximum segment size is unset.</exception>
    public ZstdSegmentCodec(int level, int maximumSegmentBytes)
    {
        if (level is < CompressionSettings.MinimumZstdLevel or > CompressionSettings.MaximumZstdLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "zstd levels 1-19 are permitted; levels above 19 are not (specification 10 §4).");
        }

        if (maximumSegmentBytes <= 0)
        {
            throw new ArgumentException("The maximum segment size is unset.", nameof(maximumSegmentBytes));
        }

        _compressor = new Compressor(level);

        // Bound the window to the segment size (10 §4): no window larger than
        // one segment's worth of history is ever useful, and it caps the
        // decoder memory an honest reader needs.
        _compressor.SetParameter(
            ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_windowLog,
            BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)maximumSegmentBytes)));

        _decompressor = new ZstdSegmentDecompressor();
    }

    /// <summary>
    /// Compresses a segment and applies the storage threshold
    /// (specification 10 §3). Returns <see langword="true"/> with the frame
    /// copied into <paramref name="destination"/> when the compressed form is
    /// stored; <see langword="false"/> when the record stores the plaintext
    /// unchanged under profile <c>none</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is smaller than <paramref name="plaintext"/>.</exception>
    public bool TryCompressForStorage(
        ReadOnlySpan<byte> plaintext,
        ushort thresholdPermille,
        Span<byte> destination,
        out int written)
    {
        written = 0;

        if (destination.Length < plaintext.Length)
        {
            throw new ArgumentException(
                "The destination must hold at least the plaintext length — a stored-compressed frame is never larger.",
                nameof(destination));
        }

        if (plaintext.IsEmpty)
        {
            // Zero-length segments do not exist (09 §2.1); nothing to decide.
            return false;
        }

        var bound = Compressor.GetCompressBound(plaintext.Length);
        var rented = ArrayPool<byte>.Shared.Rent(bound);

        try
        {
            var frameLength = _compressor.Wrap(plaintext, rented);

            if (!CompressionThreshold.StoreCompressed((ulong)plaintext.Length, (ulong)frameLength, thresholdPermille))
            {
                return false;
            }

            rented.AsSpan(0, frameLength).CopyTo(destination);
            written = frameLength;

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Decompresses a stored frame into exactly
    /// <paramref name="destination"/> — the record's <c>logical_length</c>.
    /// </summary>
    /// <exception cref="CompressionFormatException">
    /// The frame is malformed, produces more or fewer bytes than the record's
    /// logical length, or carries trailing input (specification 10 §4.1).
    /// </exception>
    public void Decompress(ReadOnlySpan<byte> stored, Span<byte> destination) =>
        _decompressor.Decompress(stored, destination);

    /// <inheritdoc />
    public void Dispose()
    {
        _compressor.Dispose();
        _decompressor.Dispose();
    }
}
