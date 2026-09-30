using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The implausible-time token on a line of <c>snapshots</c> (FR-GC-012,
/// contract 1.48). It marks a snapshot whose capture time does not fit the
/// order its writer published it in, and says which way it is out of step.
/// A snapshot that fits prints nothing.
/// </summary>
/// <remarks>
/// The direction is always shouted, as the clock token's is from five
/// minutes. A flag means retention keeps that snapshot on account of its
/// time, which is never a detail to read past.
/// </remarks>
[TestClass]
public sealed class SnapshotImplausibleTimeTokenTests
{
    [TestMethod]
    [DataRow("behind", "time:implausible-BEHIND")]
    [DataRow("ahead", "time:implausible-AHEAD")]
    public void AFlaggedSnapshot_SaysSoOnItsLine(string direction, string token)
    {
        var line = CliApplication.DescribeSnapshot(
            new SnapshotDescriptor(
                new string('e', 32), new string('a', 32), 42UL, 1, 3, ConsistencyMethod: 1,
                ImplausibleCaptureTime: direction));

        Assert.Contains($"  live  {token}", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ASnapshotThatFits_PrintsNothing() =>
        Assert.DoesNotContain(
            "time:",
            CliApplication.DescribeSnapshot(
                new SnapshotDescriptor(new string('e', 32), new string('a', 32), 42UL, 1, 3, ConsistencyMethod: 1)),
            StringComparison.Ordinal);

    [TestMethod]
    public void TheToken_FollowsTheClockToken()
    {
        // The clock token is how far this machine stood from a peer. This one
        // is what retention made of the time the capture recorded. Read left to
        // right, the second follows from the first.
        var line = CliApplication.DescribeSnapshot(
            new SnapshotDescriptor(
                new string('e', 32), new string('a', 32), 42UL, 1, 3, ConsistencyMethod: 1,
                ObservedClockSkewMs: 10_800_000, ImplausibleCaptureTime: "behind"));

        Assert.Contains("  clock:3h-BEHIND  time:implausible-BEHIND", line, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ADirectionThisBuildDoesNotKnow_IsPrintedAsSent()
    {
        // A newer service may name a direction this build has never heard of.
        // Dropping it would print the line of a snapshot that fits.
        var line = CliApplication.DescribeSnapshot(
            new SnapshotDescriptor(
                new string('e', 32), new string('a', 32), 42UL, 1, 3, ImplausibleCaptureTime: "sideways"));

        Assert.Contains("time:implausible-SIDEWAYS", line, StringComparison.Ordinal);
    }
}
