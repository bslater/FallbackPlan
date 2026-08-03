using System.Numerics;

namespace FallbackPlan.Domain;

/// <summary>
/// Validated <c>cdc-v1</c> segmentation parameters (specification 09 §3.1;
/// ADR-0023): a power-of-two target between 64 KiB and 16 MiB, a minimum of
/// at least target/8, and a maximum of at most target×8. The window is fixed
/// at 64 bytes in v1 and is not configurable.
/// </summary>
/// <remarks>
/// The <see langword="default"/> value of this struct is not a valid
/// parameter set; configuration validation reports it. Construct only through
/// <see cref="Create"/> or <see cref="TryCreate"/>.
/// </remarks>
public readonly struct CdcParameters : IEquatable<CdcParameters>
{
    /// <summary>The fixed v1 window size in bytes (specification 09 §3.1).</summary>
    public const int WindowSize = 64;

    /// <summary>The smallest permitted target size: 64 KiB.</summary>
    public const int MinimumTargetBytes = 64 * 1024;

    /// <summary>The largest permitted target size: 16 MiB.</summary>
    public const int MaximumTargetBytes = 16 * 1024 * 1024;

    /// <summary>The specification defaults: target 1 MiB, minimum 256 KiB, maximum 8 MiB.</summary>
    public static readonly CdcParameters Default = new(1024 * 1024, 256 * 1024, 8 * 1024 * 1024);

    private CdcParameters(int targetSize, int minSize, int maxSize)
    {
        TargetSize = targetSize;
        MinSize = minSize;
        MaxSize = maxSize;
    }

    /// <summary>The target segment size; zero for the invalid default value.</summary>
    public int TargetSize { get; }

    /// <summary>The minimum segment size — boundaries earlier than this are suppressed.</summary>
    public int MinSize { get; }

    /// <summary>The maximum segment size — a boundary is forced here unconditionally.</summary>
    public int MaxSize { get; }

    /// <summary>The boundary mask, <c>target_size − 1</c> (specification 09 §3.1).</summary>
    public ulong Mask => TargetSize == 0 ? 0 : (ulong)(TargetSize - 1);

    /// <summary>
    /// Creates a parameter set, rejecting values outside specification
    /// 09 §3.1's ranges.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The values violate 09 §3.1.</exception>
    public static CdcParameters Create(int targetSize, int minSize, int maxSize)
    {
        if (!TryCreate(targetSize, minSize, maxSize, out var parameters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetSize),
                $"cdc-v1 parameters must satisfy: target a power of two in 64 KiB – 16 MiB, "
                + $"min ≥ target/8, max ≤ target×8, min ≤ max (specification 09 §3.1); "
                + $"got target {targetSize}, min {minSize}, max {maxSize}.");
        }

        return parameters;
    }

    /// <summary>
    /// Attempts to create a parameter set; fails for values outside
    /// specification 09 §3.1's ranges.
    /// </summary>
    public static bool TryCreate(int targetSize, int minSize, int maxSize, out CdcParameters parameters)
    {
        if (targetSize is >= MinimumTargetBytes and <= MaximumTargetBytes &&
            BitOperations.IsPow2(targetSize) &&
            minSize >= targetSize / 8 &&
            maxSize <= (long)targetSize * 8 &&
            minSize <= maxSize)
        {
            parameters = new CdcParameters(targetSize, minSize, maxSize);
            return true;
        }

        parameters = default;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(CdcParameters other) =>
        TargetSize == other.TargetSize && MinSize == other.MinSize && MaxSize == other.MaxSize;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CdcParameters other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TargetSize, MinSize, MaxSize);

    /// <inheritdoc />
    public override string ToString() => $"cdc-v1 target {TargetSize}, min {MinSize}, max {MaxSize}";

    public static bool operator ==(CdcParameters left, CdcParameters right) => left.Equals(right);

    public static bool operator !=(CdcParameters left, CdcParameters right) => !left.Equals(right);
}
