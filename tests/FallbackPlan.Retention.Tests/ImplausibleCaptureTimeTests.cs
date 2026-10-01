using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A snapshot whose capture time a wrong clock made implausible is flagged and
/// kept rather than silently expired (FR-GC-012, architecture 04 §7,
/// ADR-0078).
/// </summary>
/// <remarks>
/// <para>
/// Implausible means out of step with the order its writer published it in,
/// by more than the configured skew margin. The yardstick is the longest run
/// of that writer's captures whose times rise with their publication. The
/// machine's clock only decides which captures may anchor that run; no
/// capture is judged on the clock alone.
/// </para>
/// <para>
/// A flagged capture is kept whatever the rules say. It holds no
/// min-generations place and represents no bucket, and the selection names it
/// with the direction it is out of step in.
/// </para>
/// </remarks>
[TestClass]
public sealed class ImplausibleCaptureTimeTests
{
    private const string Writer = "11111111111111111111111111111111";
    private const string Behind = "implausible capture time (dated before earlier publications)";
    private const string Ahead = "implausible capture time (dated after later publications)";

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Margin = TimeSpan.FromDays(1);
    private static readonly DateTimeOffset ClockReset = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AClockSetBackThenRestored_KeepsWhatItMisdated_AndSaysWhy()
    {
        // Ten daily captures, then three taken while the clock read 2001 after
        // a firmware reset, then two after it was put right. By their recorded
        // times the three were twenty-five years old and expired with nothing
        // said, although they were the machine's newest state when taken.
        var history = Daily(first: 1, count: 10, from: Now.AddDays(-14));
        var misdated = Daily(first: 11, count: 3, from: ClockReset);
        var restored = Daily(first: 14, count: 2, from: Now.AddDays(-2));

        var selection = RetentionPlanner.Select(
            [.. history, .. misdated, .. restored],
            new RetentionConfiguration { KeepDaily = 30, MinGenerations = 2 },
            Now,
            Margin);

        CollectionAssert.AreEquivalent(
            misdated.ToList(), selection.Implausible.Select(implausible => implausible.Snapshot).ToList());
        Assert.IsTrue(selection.Implausible.All(implausible => implausible.Direction == ImplausibleCaptureTime.Behind));
        foreach (var snapshot in misdated)
        {
            Assert.Contains(Behind, selection.Keep.Single(keep => keep.Snapshot == snapshot).Reasons);
        }

        Assert.IsEmpty(selection.Expire);
    }

    [TestMethod]
    public void AClockSetAheadThenRestored_FlagsTheFutureDatedCapture_AndTheFloorHoldsTheRealNewest()
    {
        // With a floor of one and no other rule, the newest capture is what
        // stays. One taken while the clock read ten years ahead sorted newest
        // and took that place, and the real newest, captured once the clock
        // was put right, expired.
        var before = Daily(first: 1, count: 3, from: Now.AddDays(-5));
        var ahead = Published(4, Now.AddYears(10));
        var newest = Published(5, Now.AddHours(-1));

        var selection = RetentionPlanner.Select(
            [.. before, ahead, newest], new RetentionConfiguration { MinGenerations = 1 }, Now, Margin);

        var flagged = Assert.ContainsSingle(selection.Implausible);
        Assert.AreEqual(ahead, flagged.Snapshot);
        Assert.AreEqual(ImplausibleCaptureTime.Ahead, flagged.Direction);
        Assert.Contains(Ahead, selection.Keep.Single(keep => keep.Snapshot == ahead).Reasons);
        Assert.Contains("min-generations", selection.Keep.Single(keep => keep.Snapshot == newest).Reasons);
        CollectionAssert.AreEquivalent(before.ToList(), selection.Expire.ToList());
    }

    [TestMethod]
    public void AMisdatedCapture_NeverFillsAFloorPlace()
    {
        var history = Daily(first: 1, count: 5, from: Now.AddDays(-6));
        var misdated = Published(6, ClockReset);

        var selection = RetentionPlanner.Select(
            [.. history, misdated], new RetentionConfiguration { MinGenerations = 2 }, Now, Margin);

        // The floor's two places go to the two newest captures it can trust.
        // The misdated one is kept by its flag, not by the floor.
        var misdatedKeep = selection.Keep.Single(keep => keep.Snapshot == misdated);
        Assert.Contains(Behind, misdatedKeep.Reasons);
        Assert.DoesNotContain("min-generations", misdatedKeep.Reasons);
        Assert.Contains("min-generations", selection.Keep.Single(keep => keep.Snapshot == history[4]).Reasons);
        Assert.Contains("min-generations", selection.Keep.Single(keep => keep.Snapshot == history[3]).Reasons);
        CollectionAssert.AreEquivalent(history.Take(3).ToList(), selection.Expire.ToList());
    }

    [TestMethod]
    public void AMisdatedCapture_TakesNoBucketFromTheCaptureThatBelongsThere()
    {
        // A clock set back three days, for one capture: it is dated to a day
        // that already has a capture of its own, and later that day. Taken at
        // its word it would represent that day, and the real capture of that
        // day would expire.
        var fiveDaysAgo = Published(1, Now.AddDays(-5).AddHours(-6));
        var rest = new[] { Published(2, Now.AddDays(-4)), Published(3, Now.AddDays(-3)), Published(4, Now.AddDays(-2)) };
        var misdated = Published(5, Now.AddDays(-5).AddHours(6));
        var newest = Published(6, Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [fiveDaysAgo, .. rest, misdated, newest], new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        Assert.AreEqual(misdated, Assert.ContainsSingle(selection.Implausible).Snapshot);
        var day = string.Create(CultureInfo.InvariantCulture, $"daily {Now.AddDays(-5):yyyy-MM-dd}");
        Assert.Contains(day, selection.Keep.Single(keep => keep.Snapshot == fiveDaysAgo).Reasons);
        Assert.DoesNotContain(day, selection.Keep.Single(keep => keep.Snapshot == misdated).Reasons);
        Assert.IsEmpty(selection.Expire);
    }

    [TestMethod]
    public void AClockStepInsideTheMargin_FlagsNothing_AndThePolicyDecidesAsBefore()
    {
        // A clock corrected by two hours between captures is a time sync, not
        // a broken clock.
        var first = Published(1, Now.AddDays(-2));
        var stepped = Published(2, Now.AddDays(-2).AddHours(-2));
        var next = Published(3, Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [first, stepped, next], new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        Assert.IsEmpty(selection.Implausible);

        // The stepped capture shares a day with the one before it, and the
        // newer of the two by capture time represents that day, as it always
        // has.
        Assert.AreEqual(stepped, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void TheMargin_IsTheOneConfigured_AndADayWhenNoneIsGiven()
    {
        // A clock stepped back three days between captures.
        var first = Published(1, Now.AddDays(-2));
        var stepped = Published(2, Now.AddDays(-5));
        var next = Published(3, Now.AddDays(-1));
        var policy = new RetentionConfiguration { KeepDaily = 30 };

        Assert.AreEqual(
            stepped, Assert.ContainsSingle(RetentionPlanner.Select([first, stepped, next], policy, Now, Margin).Implausible).Snapshot);
        Assert.IsEmpty(RetentionPlanner.Select([first, stepped, next], policy, Now, TimeSpan.FromDays(7)).Implausible);
        Assert.AreEqual(
            stepped, Assert.ContainsSingle(RetentionPlanner.Select([first, stepped, next], policy, Now).Implausible).Snapshot);
    }

    [TestMethod]
    public void PublicationOrder_IsAWritersOwn()
    {
        // An archive adopted under a new identity (ADR-0061 §4). The new
        // writer's counters start again near 1, below the old writer's, while
        // its captures are the newest. Read as one order, every one of them
        // would be out of step.
        const string OldWriter = "22222222222222222222222222222222";
        const string NewWriter = "33333333333333333333333333333333";
        var old = Enumerable.Range(0, 5)
            .Select(day => Published((ulong)(100 + day), Now.AddDays(-20 + day), OldWriter))
            .ToList();
        var adopted = Enumerable.Range(0, 3)
            .Select(day => Published((ulong)(1 + day), Now.AddDays(-3 + day), NewWriter))
            .ToList();

        var selection = RetentionPlanner.Select(
            [.. old, .. adopted], new RetentionConfiguration { KeepDaily = 30 }, Now, Margin);

        Assert.IsEmpty(selection.Implausible);
    }

    [TestMethod]
    public void ACaptureAheadOfThisClock_ThatNothingLaterContradicts_IsNotFlagged()
    {
        // This machine's clock reads a week behind every capture. Either it
        // was set back since, or the captures' clock ran ahead, and nothing in
        // the order says which. A capture is not judged on this clock alone.
        var captures = Daily(first: 1, count: 3, from: Now.AddDays(7));

        var selection = RetentionPlanner.Select(captures, new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        Assert.IsEmpty(selection.Implausible);

        // Dated after the window opened, each is inside it and represents its
        // own day, as a capture from the future always has.
        Assert.HasCount(3, selection.Keep);
    }

    [TestMethod]
    public void WhereTwoRunsAreAsLong_TheLaterTimesAreTakenAsTheTruth()
    {
        // Two captures dated to last week, then two dated to last year: one
        // pair is wrong, and the order cannot say which. Taking the later pair
        // as the truth flags the earlier-dated one, which is then kept. The
        // other way round, the earlier-dated pair would be judged a year old,
        // and could expire.
        var recent = new[] { Published(1, Now.AddDays(-8)), Published(2, Now.AddDays(-7)) };
        var lastYear = new[] { Published(3, Now.AddDays(-400)), Published(4, Now.AddDays(-399)) };

        var selection = RetentionPlanner.Select(
            [.. recent, .. lastYear], new RetentionConfiguration { KeepDaily = 30 }, Now, Margin);

        CollectionAssert.AreEquivalent(
            lastYear.ToList(), selection.Implausible.Select(implausible => implausible.Snapshot).ToList());
        Assert.IsTrue(selection.Implausible.All(implausible => implausible.Direction == ImplausibleCaptureTime.Behind));
        Assert.IsEmpty(selection.Expire);
    }

    [TestMethod]
    public void ACaptureWithNoPublicationSequence_IsNotJudged()
    {
        // A caller that cannot say where a snapshot sits in its writer's order
        // gets the old reading, never a guess.
        var yesterday = new SnapshotFact("yesterday", (ulong)Now.AddDays(-1).ToUnixTimeMilliseconds());
        var reset = new SnapshotFact("reset", (ulong)ClockReset.ToUnixTimeMilliseconds());

        var selection = RetentionPlanner.Select(
            [yesterday, reset], new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        Assert.IsEmpty(selection.Implausible);
        Assert.AreEqual(reset, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void ACaptureDatedPastTheCalendar_IsFlagged_WhereBeforeItThrew()
    {
        // About the year 11476: a stamp DateTimeOffset cannot hold, which the
        // window rules used to hand straight to it.
        var before = Published(1, Now.AddDays(-2));
        var beyond = new SnapshotFact("beyond", 300_000_000_000_000, 2, 1, Writer);
        var after = Published(3, Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [before, beyond, after], new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        var flagged = Assert.ContainsSingle(selection.Implausible);
        Assert.AreEqual(beyond, flagged.Snapshot);
        Assert.AreEqual(ImplausibleCaptureTime.Ahead, flagged.Direction);
    }

    [TestMethod]
    public void ACaptureDatedPastTheCalendar_WithNothingAfterIt_IsKeptAsAFutureCaptureIs()
    {
        // The largest stamp a manifest can carry used to read as 1969 and
        // expire as ancient. Nothing later contradicts it, so it is judged as
        // any capture from the future is: inside every window.
        var before = Published(1, Now.AddDays(-2));
        var beyond = new SnapshotFact("beyond", ulong.MaxValue, 2, 1, Writer);

        var selection = RetentionPlanner.Select(
            [before, beyond], new RetentionConfiguration { KeepDaily = 7 }, Now, Margin);

        Assert.IsEmpty(selection.Implausible);
        Assert.Contains(keep => keep.Snapshot == beyond, selection.Keep);
    }

    [TestMethod]
    public void TheFinding_IsTheSameWhateverThePolicy()
    {
        // The listing and every destination's keep-set ask the same question
        // of the same captures. A policy decides what expires, never what is
        // implausible.
        var history = Daily(first: 1, count: 4, from: Now.AddDays(-6));
        var misdated = Published(5, ClockReset);
        IReadOnlyList<SnapshotFact> facts = [.. history, misdated];

        var found = RetentionPlanner.FindImplausible(facts, Now, Margin);

        Assert.AreEqual(misdated, Assert.ContainsSingle(found).Snapshot);
        foreach (var policy in new[]
                 {
                     new RetentionConfiguration(),
                     new RetentionConfiguration { MinGenerations = 1 },
                     new RetentionConfiguration { KeepDaily = 1, KeepMonthly = 12 },
                 })
        {
            var selection = RetentionPlanner.Select(facts, policy, Now, Margin);
            CollectionAssert.AreEqual(found.ToList(), selection.Implausible.ToList());
            Assert.Contains(keep => keep.Snapshot == misdated, selection.Keep);
        }
    }

    private static SnapshotFact Published(ulong sequence, DateTimeOffset capturedAt, string writer = Writer) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"{writer[..4]}-{sequence:d4}"),
            (ulong)capturedAt.ToUnixTimeMilliseconds(),
            sequence,
            1,
            writer);

    /// <summary>One capture a day, published in that order under consecutive sequences.</summary>
    private static List<SnapshotFact> Daily(ulong first, int count, DateTimeOffset from) =>
        [.. Enumerable.Range(0, count).Select(day => Published(first + (ulong)day, from.AddDays(day)))];
}
