using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The clock token on a line of <c>snapshots</c> (NFR-TIME-002, contract
/// 1.47): how far the capturing machine's clock stood from its peer's when
/// the snapshot was taken, or nothing when it had no peer to compare with.
/// </summary>
/// <remarks>
/// <para>
/// The direction is relative to the peer — behind it or ahead of it — and
/// never "slow" or "fast", because a disagreement between two clocks does not
/// say which of them is wrong.
/// </para>
/// <para>
/// Under two seconds is in step: the reading's own uncertainty is half a
/// round trip, and a finer claim would be noise. From five minutes the
/// direction is shouted, as the other alarm tokens are, because that is where
/// timestamps from this machine stop being trustworthy for anything that
/// compares them with another's.
/// </para>
/// </remarks>
[TestClass]
public sealed class SnapshotClockTokenTests
{
    [TestMethod]
    public void ASnapshotWithNoReading_PrintsNothing() =>
        Assert.IsNull(CliApplication.DescribeClock(null));

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1_999L)]
    [DataRow(-1_999L)]
    public void UnderTwoSeconds_IsInStep(long skew) =>
        Assert.AreEqual("clock:in-step", CliApplication.DescribeClock(skew));

    [TestMethod]
    [DataRow(2_000L, "clock:2s-behind")]
    [DataRow(2_999L, "clock:2s-behind")]
    [DataRow(252_000L, "clock:4m12s-behind")]
    [DataRow(-90_000L, "clock:1m30s-ahead")]
    [DataRow(-240_000L, "clock:4m-ahead")]
    public void UnderFiveMinutes_NamesTheDirectionQuietly(long skew, string token) =>
        Assert.AreEqual(token, CliApplication.DescribeClock(skew));

    [TestMethod]
    public void TheToken_RidesTheSnapshotLine_AfterHowTheCaptureWasTaken()
    {
        var observed = CliApplication.DescribeSnapshot(
            new SnapshotDescriptor(
                new string('e', 32), new string('a', 32), 42UL, 1, 3, ConsistencyMethod: 1,
                ObservedClockSkewMs: 10_800_000));

        Assert.Contains("  live  clock:3h-BEHIND", observed, StringComparison.Ordinal);

        var unobserved = CliApplication.DescribeSnapshot(
            new SnapshotDescriptor(new string('e', 32), new string('a', 32), 42UL, 1, 3, ConsistencyMethod: 1));

        Assert.DoesNotContain("clock:", unobserved, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(300_000L, "clock:5m-BEHIND")]
    [DataRow(10_800_000L, "clock:3h-BEHIND")]
    [DataRow(7_500_000L, "clock:2h5m-BEHIND")]
    [DataRow(-93_600_000L, "clock:1d2h-AHEAD")]
    [DataRow(-172_800_000L, "clock:2d-AHEAD")]
    public void FromFiveMinutes_ShoutsTheDirection(long skew, string token) =>
        Assert.AreEqual(token, CliApplication.DescribeClock(skew));
}
