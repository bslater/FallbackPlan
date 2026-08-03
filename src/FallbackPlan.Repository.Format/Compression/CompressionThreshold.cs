namespace FallbackPlan.Repository.Format.Compression;

/// <summary>
/// The storage-threshold decision (specification 10 §3; FR-ARCH-005): the
/// compressed form is stored only when
/// <c>compressed_length ≤ logical_length × (1000 − threshold_permille) / 1000</c>.
/// </summary>
/// <remarks>
/// Implemented as the exact rational comparison
/// <c>compressed × 1000 ≤ logical × (1000 − t)</c> rather than with integer
/// division, so two implementations cannot disagree at the boundary. Records
/// are at most 64 MiB, so the products cannot overflow a <c>u64</c>.
/// </remarks>
public static class CompressionThreshold
{
    /// <summary>
    /// Returns whether the compressed form is stored for these lengths under
    /// <paramref name="thresholdPermille"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thresholdPermille"/> exceeds 1000.</exception>
    public static bool StoreCompressed(ulong logicalLength, ulong compressedLength, ushort thresholdPermille)
    {
        if (thresholdPermille > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdPermille),
                thresholdPermille,
                "The threshold is a permille value, 0–1000 (specification 10 §3).");
        }

        return compressedLength * 1000UL <= logicalLength * (1000UL - thresholdPermille);
    }
}
