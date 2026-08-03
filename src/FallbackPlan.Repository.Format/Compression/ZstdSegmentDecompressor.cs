using ZstdSharp;

namespace FallbackPlan.Repository.Format.Compression;

/// <summary>
/// The read side of <c>zstd-v1</c> (specification 10 §4.1), usable without
/// any writer configuration: a reader knows only the record's
/// <c>logical_length</c>, which is the exact output size it enforces. The
/// decoder window is capped at 2<sup>26</sup> — the 64 MiB record limit — so
/// a hostile frame header cannot demand unbounded memory.
/// </summary>
/// <remarks>Wraps one stateful decompressor context; not thread-safe.</remarks>
public sealed class ZstdSegmentDecompressor : IDisposable
{
    private readonly Decompressor _decompressor = new();

    /// <summary>Creates a decompressor with the bounded window.</summary>
    public ZstdSegmentDecompressor()
    {
        _decompressor.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 26);
    }

    /// <summary>
    /// Decompresses a stored frame into exactly
    /// <paramref name="destination"/> — the record's <c>logical_length</c>.
    /// </summary>
    /// <exception cref="CompressionFormatException">
    /// The frame is malformed, produces more or fewer bytes than the record's
    /// logical length, or carries trailing input (specification 10 §4.1).
    /// </exception>
    public void Decompress(ReadOnlySpan<byte> stored, Span<byte> destination)
    {
        int produced;

        try
        {
            // The library refuses output beyond the destination (the bomb
            // defence) and refuses trailing input bytes after the frame —
            // both verified behaviours, both mapped to the same refusal.
            produced = _decompressor.Unwrap(stored, destination);
        }
        catch (ZstdException exception)
        {
            throw new CompressionFormatException(
                "Stored bytes are not one standard Zstandard frame producing exactly the record's logical length (specification 10 §4.1).",
                exception);
        }

        if (produced != destination.Length)
        {
            throw new CompressionFormatException(
                $"The frame produced {produced} bytes; the record's logical length is {destination.Length} (specification 10 §4.1).");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _decompressor.Dispose();
}
