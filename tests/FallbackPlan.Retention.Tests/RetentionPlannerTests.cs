using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Retention selects and deletes nothing (architecture 07 §1, FR-GC-001):
/// a pure derivation from snapshots and policy to a keep-set with stated
/// reasons — the dry-run report's raw material (FR-GC-005).
/// </summary>
[TestClass]
public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Select_NoRuleConfigured_ExpiresNothing()
    {
        var snapshots = Days(400, 200, 10, 1);

        var selection = RetentionPlanner.Select(snapshots, new RetentionConfiguration(), Now);

        // An absent policy is "keep everything", never "keep nothing" — the
        // default must not be the destructive reading.
        Assert.HasCount(4, selection.Keep);
        Assert.IsEmpty(selection.Expire);
    }

    [TestMethod]
    public void Select_KeepDaily_KeepsTheNewestPerDayInsideTheWindowAndExpiresBeyondIt()
    {
        // Two snapshots on the same day: only the newer survives the daily
        // rule. One beyond the window: expired.
        var sameDayEarlier = At(Now.AddDays(-1).AddHours(-2));
        var sameDayLater = At(Now.AddDays(-1));
        var beyond = At(Now.AddDays(-9));

        var selection = RetentionPlanner.Select(
            [sameDayEarlier, sameDayLater, beyond],
            new RetentionConfiguration { KeepDaily = 7 },
            Now);

        Assert.Contains(keep => keep.Snapshot == sameDayLater, selection.Keep);
        Assert.Contains(snapshot => snapshot == sameDayEarlier, selection.Expire);
        Assert.Contains(snapshot => snapshot == beyond, selection.Expire);
    }

    [TestMethod]
    public void Select_MinGenerations_IsTheFloorTheOtherRulesCannotOverride()
    {
        // Everything is far outside every window, yet the newest three stay:
        // a misconfigured schedule or a long offline period must not leave a
        // set with nothing (architecture 07 §2).
        var snapshots = Days(400, 300, 200, 100);

        var selection = RetentionPlanner.Select(
            snapshots,
            new RetentionConfiguration { KeepDaily = 1, MinGenerations = 3 },
            Now);

        Assert.HasCount(3, selection.Keep);
        Assert.ContainsSingle(selection.Expire);
        Assert.AreEqual(snapshots[0], Assert.ContainsSingle(selection.Expire));
        Assert.IsTrue(selection.Keep.All(keep => keep.Reasons.Count > 0));
    }

    [TestMethod]
    public void Select_SeveralRules_KeepsTheUnionWithEveryReasonNamed()
    {
        var today = At(Now.AddHours(-1));
        var lastWeek = At(Now.AddDays(-6));
        var lastMonth = At(Now.AddDays(-20));
        var ancient = At(Now.AddDays(-400));

        var selection = RetentionPlanner.Select(
            [ancient, lastMonth, lastWeek, today],
            new RetentionConfiguration { KeepDaily = 2, KeepWeekly = 2, KeepMonthly = 2, MinGenerations = 1 },
            Now);

        // The newest snapshot satisfies several rules at once, and the report
        // says all of them — the dry-run must explain, not merely list.
        var newest = selection.Keep.Single(keep => keep.Snapshot == today);
        Assert.IsGreaterThanOrEqualTo(2, newest.Reasons.Count);

        Assert.Contains(snapshot => snapshot == ancient, selection.Expire);
    }

    [TestMethod]
    public void Select_TheSameInstant_BreaksTheTieDeterministically()
    {
        var first = new SnapshotFact(new string('a', 64), (ulong)Now.AddDays(-1).ToUnixTimeMilliseconds());
        var second = new SnapshotFact(new string('b', 64), (ulong)Now.AddDays(-1).ToUnixTimeMilliseconds());

        var one = RetentionPlanner.Select([first, second], new RetentionConfiguration { KeepDaily = 7 }, Now);
        var two = RetentionPlanner.Select([second, first], new RetentionConfiguration { KeepDaily = 7 }, Now);

        // Input order must not decide what survives: two runs over the same
        // facts produce the same plan, or the dry-run report is a lottery.
        Assert.AreEqual(
            Assert.ContainsSingle(one.Keep).Snapshot,
            Assert.ContainsSingle(two.Keep).Snapshot);
    }

    private static SnapshotFact At(DateTimeOffset capturedAt) =>
        new(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, 'f'),
            (ulong)capturedAt.ToUnixTimeMilliseconds());

    /// <summary>Snapshots captured the given number of days ago, oldest first.</summary>
    private static IReadOnlyList<SnapshotFact> Days(params int[] daysAgo) =>
        [.. daysAgo.OrderByDescending(days => days).Select(days => At(Now.AddDays(-days)))];
}
