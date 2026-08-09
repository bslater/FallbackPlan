using Bodu;
using System.Numerics;
using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// The <c>fixed-v1</c> boundary arithmetic (specification 09 §2; FR-ARCH-001):
/// segment <em>i</em> covers <c>[i·S, min((i+1)·S, L))</c>, every segment has
/// length exactly <em>S</em> except the last, and a file of length zero
/// produces no segments at all.
/// </summary>
/// <remarks>
/// Pure arithmetic over a power-of-two segment size — shifts, never division
/// (09 §2.2) — with no I/O and no allocation, so boundary questions about a
/// multi-tebibyte file cost nothing (NFR-PERF-001 is about the streaming
/// path; the arithmetic here is exact at any length).
/// </remarks>
public static class FixedSegmentation
{
    /// <summary>Returns the number of segments in a file of <paramref name="fileLength"/> bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative or the segment size unset.</exception>
    public static long SegmentCount(long fileLength, SegmentSize segmentSize)
    {
        ValidateArguments(fileLength, segmentSize);

        var shift = BitOperations.Log2((uint)segmentSize.Bytes);

        return (fileLength + segmentSize.Bytes - 1) >> shift;
    }

    /// <summary>
    /// Returns the byte range of segment <paramref name="index"/> in a file of
    /// <paramref name="fileLength"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index does not name a segment of the file.</exception>
    public static (long Offset, long Length) GetSegment(long index, long fileLength, SegmentSize segmentSize)
    {
        ValidateArguments(fileLength, segmentSize);

        if (index < 0 || index >= SegmentCount(fileLength, segmentSize))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index does not name a segment of the file.");
        }

        var offset = index * segmentSize.Bytes;

        return (offset, Math.Min(segmentSize.Bytes, fileLength - offset));
    }

    /// <summary>
    /// Returns the index of the segment containing byte
    /// <paramref name="byteOffset"/> — <c>N &gt;&gt; log2(S)</c>
    /// (specification 09 §2.2).
    /// </summary>
    public static long SegmentIndexOfOffset(long byteOffset, SegmentSize segmentSize)
    {
        ValidateArguments(byteOffset, segmentSize);

        return byteOffset >> BitOperations.Log2((uint)segmentSize.Bytes);
    }

    private static void ValidateArguments(long length, SegmentSize segmentSize)
    {
        ThrowHelper.ThrowIfNegative(length);

        if (segmentSize.Bytes == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentSize),
                "The segment size is unset; construct it via SegmentSize.Create (specification 09 §2.2).");
        }
    }
}
