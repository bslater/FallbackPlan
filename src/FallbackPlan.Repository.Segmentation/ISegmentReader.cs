namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// A pull-based segmenter over a stream (specification 09 §6 step 2): the
/// caller supplies one buffer of at least <see cref="RequiredBufferSize"/>
/// bytes, each call fills its head with the next segment's plaintext, and
/// memory stays bounded by the largest possible segment (NFR-PERF-001).
/// </summary>
public interface ISegmentReader
{
    /// <summary>The minimum buffer size a caller must supply — the largest possible segment.</summary>
    int RequiredBufferSize { get; }

    /// <summary>
    /// Reads the next segment into the head of <paramref name="buffer"/>.
    /// Returns <see langword="null"/> when the source is exhausted.
    /// </summary>
    ValueTask<SegmentDescriptor?> ReadNextAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
