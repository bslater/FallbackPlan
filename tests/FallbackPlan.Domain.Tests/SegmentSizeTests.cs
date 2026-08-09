namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the fixed-v1 segment-size rules (specification 09 §2.2): a power
/// of two between 64 KiB and 64 MiB, everything else refused.
/// </summary>
[TestClass]
public sealed class SegmentSizeTests
{
    [TestMethod]
    [DataRow(64 * 1024)]
    [DataRow(1024 * 1024)]
    [DataRow(64 * 1024 * 1024)]
    public void Powers_of_two_inside_the_range_are_accepted(int bytes)
    {
        Assert.IsTrue(SegmentSize.TryCreate(bytes, out var size));
        Assert.AreEqual(bytes, size.Bytes);
        Assert.AreEqual(bytes, SegmentSize.Create(bytes).Bytes);
    }

    [TestMethod]
    [DataRow(32 * 1024)]        // below the minimum
    [DataRow(96 * 1024)]        // in range but not a power of two
    [DataRow(1024 * 1024 + 1)]  // not a power of two
    [DataRow(128 * 1024 * 1024)] // above the maximum
    [DataRow(0)]
    [DataRow(-1024)]
    public void Everything_else_is_refused(int bytes)
    {
        Assert.IsFalse(SegmentSize.TryCreate(bytes, out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SegmentSize.Create(bytes));
    }

    [TestMethod]
    public void SegmentSize_TheSpecificationDefault_IsOneMebibyte()
    {
        Assert.AreEqual(1024 * 1024, SegmentSize.Default.Bytes);
    }

    [TestMethod]
    public void SegmentSize_TheDefaultStructValue_IsNotValid()
    {
        Assert.AreEqual(0, default(SegmentSize).Bytes);
    }
}
