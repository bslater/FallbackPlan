namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the fixed-v1 segment-size rules (specification 09 §2.2): a power
/// of two between 64 KiB and 64 MiB, everything else refused.
/// </summary>
public sealed class SegmentSizeTests
{
    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(64 * 1024 * 1024)]
    public void Powers_of_two_inside_the_range_are_accepted(int bytes)
    {
        Assert.True(SegmentSize.TryCreate(bytes, out var size));
        Assert.Equal(bytes, size.Bytes);
        Assert.Equal(bytes, SegmentSize.Create(bytes).Bytes);
    }

    [Theory]
    [InlineData(32 * 1024)]        // below the minimum
    [InlineData(96 * 1024)]        // in range but not a power of two
    [InlineData(1024 * 1024 + 1)]  // not a power of two
    [InlineData(128 * 1024 * 1024)] // above the maximum
    [InlineData(0)]
    [InlineData(-1024)]
    public void Everything_else_is_refused(int bytes)
    {
        Assert.False(SegmentSize.TryCreate(bytes, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentSize.Create(bytes));
    }

    [Fact]
    public void The_specification_default_is_one_mebibyte()
    {
        Assert.Equal(1024 * 1024, SegmentSize.Default.Bytes);
    }

    [Fact]
    public void The_default_struct_value_is_not_a_valid_segment_size()
    {
        Assert.Equal(0, default(SegmentSize).Bytes);
    }
}
