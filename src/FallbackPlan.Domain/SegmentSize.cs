using System.Numerics;

namespace FallbackPlan.Domain;

/// <summary>
/// A <c>fixed-v1</c> segment size (specification 09 §2.2): a power of two
/// between 64 KiB and 64 MiB. The power-of-two rule lets a reader map a byte
/// offset to a segment with a shift, and a size outside the range must be
/// rejected rather than honoured.
/// </summary>
/// <remarks>
/// The <see langword="default"/> value of this struct is not a valid segment
/// size; configuration validation reports it as out of range. Construct only
/// through <see cref="Create"/> or <see cref="TryCreate"/>.
/// </remarks>
public readonly struct SegmentSize : IEquatable<SegmentSize>
{
    /// <summary>The smallest permitted segment size: 64 KiB.</summary>
    public const int MinimumBytes = 64 * 1024;

    /// <summary>The largest permitted segment size: 64 MiB.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;

    /// <summary>The specification default: 1 MiB.</summary>
    public static readonly SegmentSize Default = new(1024 * 1024);

    private SegmentSize(int bytes) => Bytes = bytes;

    /// <summary>The segment size in bytes; zero for the invalid default value.</summary>
    public int Bytes { get; }

    /// <summary>
    /// Creates a segment size, rejecting values that are not a power of two or
    /// fall outside 64 KiB – 64 MiB.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is not a valid segment size.</exception>
    public static SegmentSize Create(int bytes)
    {
        if (!TryCreate(bytes, out var size))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                bytes,
                "A fixed-v1 segment size must be a power of two between 64 KiB and 64 MiB (specification 09 §2.2).");
        }

        return size;
    }

    /// <summary>
    /// Attempts to create a segment size; fails for values that are not a
    /// power of two or fall outside 64 KiB – 64 MiB.
    /// </summary>
    public static bool TryCreate(int bytes, out SegmentSize size)
    {
        if (bytes is >= MinimumBytes and <= MaximumBytes && BitOperations.IsPow2(bytes))
        {
            size = new SegmentSize(bytes);
            return true;
        }

        size = default;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(SegmentSize other) => Bytes == other.Bytes;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SegmentSize other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Bytes;

    /// <inheritdoc />
    public override string ToString() => $"{Bytes} bytes";

    public static bool operator ==(SegmentSize left, SegmentSize right) => left.Equals(right);

    public static bool operator !=(SegmentSize left, SegmentSize right) => !left.Equals(right);
}
