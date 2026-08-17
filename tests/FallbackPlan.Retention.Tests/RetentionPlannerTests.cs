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

    /// <summary>
    /// A partial capture must not be able to satisfy the floor on its own
    /// (ledger O6 — a partial backup producing a defective one once retention
    /// rules apply).
    /// </summary>
    /// <remarks>
    /// This is the shape that costs data. <c>min_generations</c> exists so a
    /// set is never left with nothing usable; a floor filled by a backup that
    /// did not back everything up is not that guarantee, it is the appearance
    /// of it. Compounded with RN-F3 — before which nothing above the manifest
    /// could even tell the two apart — the set is left holding one snapshot
    /// with a hole in it and every complete one expired.
    /// </remarks>
    [TestMethod]
    public void Select_MinGenerations_CountsCompleteCapturesOnly()
    {
        var complete = At(Now.AddDays(-200));
        var partial = Partial(Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [complete, partial],
            new RetentionConfiguration { MinGenerations = 1 },
            Now);

        Assert.Contains(
            keep => keep.Snapshot == complete,
            selection.Keep,
            "the only complete backup expired because a partial one filled the floor");

        // The partial is kept too. It holds most of the data and deleting it
        // would be a second wrong; the rule only ever keeps more.
        Assert.Contains(keep => keep.Snapshot == partial, selection.Keep);
        Assert.IsEmpty(selection.Expire);
    }

    /// <summary>
    /// Complete means status 1 — not "not partial". The format assigns 3 to an
    /// aborted capture (specification 06 §snapshot-manifest) and the codec
    /// admits it on read; this implementation never writes it, but another
    /// writer may, and an aborted backup filling the floor is the same data
    /// loss as a partial one doing it.
    /// </summary>
    [TestMethod]
    public void Select_MinGenerations_DoesNotLetAnAbortedCaptureFillTheFloor()
    {
        var complete = At(Now.AddDays(-200));
        var aborted = At(Now.AddDays(-1)) with { CaptureStatus = 3 };

        var selection = RetentionPlanner.Select(
            [complete, aborted],
            new RetentionConfiguration { MinGenerations = 1 },
            Now);

        Assert.IsFalse(aborted.IsComplete, "an aborted capture read as complete");
        Assert.Contains(
            keep => keep.Snapshot == complete,
            selection.Keep,
            "the only complete backup expired because an aborted capture filled the floor");
        Assert.IsFalse(
            selection.Keep.Any(keep =>
                keep.Snapshot == aborted
                && keep.Reasons.Any(reason => reason.Contains("complete", StringComparison.Ordinal))),
            "an aborted capture was kept AS the complete representative of its bucket");
    }

    /// <summary>A floor of two means the two newest complete captures, whatever sits between them.</summary>
    [TestMethod]
    public void Select_MinGenerations_ReachesPastPartialsToFillTheFloor()
    {
        var oldest = At(Now.AddDays(-400));
        var olderComplete = At(Now.AddDays(-300));
        var newerComplete = At(Now.AddDays(-200));
        var partial = Partial(Now.AddDays(-100));

        var selection = RetentionPlanner.Select(
            [oldest, olderComplete, newerComplete, partial],
            new RetentionConfiguration { MinGenerations = 2 },
            Now);

        Assert.Contains(keep => keep.Snapshot == newerComplete, selection.Keep);
        Assert.Contains(keep => keep.Snapshot == olderComplete, selection.Keep);
        Assert.Contains(keep => keep.Snapshot == partial, selection.Keep, "the partial was discarded");
        Assert.AreEqual(oldest, Assert.ContainsSingle(selection.Expire));
    }

    /// <summary>
    /// A bucket whose newest snapshot is partial also keeps that bucket's
    /// newest complete one, so no day, week or month is represented only by a
    /// backup with a hole in it.
    /// </summary>
    [TestMethod]
    public void Select_ADayWhoseNewestIsPartial_AlsoKeepsThatDaysNewestCompleteCapture()
    {
        var earlierComplete = At(Now.AddDays(-1).AddHours(-4));
        var laterPartial = Partial(Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [earlierComplete, laterPartial],
            new RetentionConfiguration { KeepDaily = 7 },
            Now);

        Assert.Contains(keep => keep.Snapshot == laterPartial, selection.Keep);

        var complete = selection.Keep.SingleOrDefault(keep => keep.Snapshot == earlierComplete);
        Assert.IsNotNull(
            complete,
            "the day's only complete backup expired in favour of a partial one captured later");
        Assert.Contains(
            reason => reason.Contains("complete", StringComparison.Ordinal),
            complete.Reasons,
            "the report does not say why the extra snapshot was kept");
    }

    /// <summary>
    /// A history with no complete capture in it at all keeps exactly what the
    /// old rules kept. There is no complete backup to fall back to, and
    /// inventing a refusal here would strand the set.
    /// </summary>
    [TestMethod]
    public void Select_AHistoryOfNothingButPartials_IsUnchanged()
    {
        var older = Partial(Now.AddDays(-2));
        var newer = Partial(Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [older, newer],
            new RetentionConfiguration { MinGenerations = 1 },
            Now);

        Assert.AreEqual(newer, Assert.ContainsSingle(selection.Keep).Snapshot);
        Assert.AreEqual(older, Assert.ContainsSingle(selection.Expire));
    }

    /// <summary>
    /// And the change is provably additive: over a history with no partial in
    /// it, the selection is identical to the one the old rules produced. The
    /// other tests in this class are the regression net; this states the
    /// property they rest on.
    /// </summary>
    [TestMethod]
    public void Select_AHistoryWithNoPartials_SelectsExactlyAsBefore()
    {
        var snapshots = Days(400, 300, 200, 100, 20, 6, 1);
        var policy = new RetentionConfiguration
        {
            KeepDaily = 2,
            KeepWeekly = 2,
            KeepMonthly = 2,
            MinGenerations = 3,
        };

        var selection = RetentionPlanner.Select(snapshots, policy, Now);

        // Every complete-capture rule is a no-op here, so no keep may carry a
        // reason the fallback invented.
        Assert.IsEmpty(
            selection.Keep.SelectMany(keep => keep.Reasons)
                .Where(reason => reason.Contains("complete", StringComparison.Ordinal)),
            "a fallback reason appeared over a history containing no partial capture");

        // Daily(2) keeps day-1; weekly(2) keeps day-1 and day-6; monthly(2)
        // keeps day-1 and day-20; the floor of 3 is already satisfied by
        // those three. Everything at 100 days and beyond expires.
        Assert.HasCount(3, selection.Keep);
        Assert.HasCount(4, selection.Expire);
    }

    private static SnapshotFact At(DateTimeOffset capturedAt) =>
        new(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, 'f'),
            (ulong)capturedAt.ToUnixTimeMilliseconds());

    /// <summary>A capture that committed a snapshot without everything in it (manifest capture_status 2).</summary>
    private static SnapshotFact Partial(DateTimeOffset capturedAt) =>
        At(capturedAt) with { CaptureStatus = 2 };

    /// <summary>Snapshots captured the given number of days ago, oldest first.</summary>
    private static IReadOnlyList<SnapshotFact> Days(params int[] daysAgo) =>
        [.. daysAgo.OrderByDescending(days => days).Select(days => At(Now.AddDays(-days)))];
}
