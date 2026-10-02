using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A snapshot a person asked to delete (FR-GC-013, ADR-0080) is expired
/// whatever the rules say, and staging lets it go only once every copy of the
/// set has been brought in line since the request.
/// </summary>
/// <remarks>
/// <para>
/// The planner reads the request off the fact, where the survey put it. A
/// requested snapshot overrides the daily, weekly and monthly windows, the
/// min-generations floor and the implausible-time flag. It holds no floor
/// place and represents no bucket, because it is going.
/// </para>
/// <para>
/// The gate holds a requested snapshot while any declared destination of the
/// set has not converged since the request. That includes one that never
/// received it, since a pass cut short may have left part of it there, and one
/// the ledger has no row for, since a missing row is not a missing copy. Only a
/// converge that runs while staging still lists those keys can remove them
/// (ADR-0034 §6, "unknown is kept"). A destination's own policy does not excuse
/// it, and the hold has no deferral bound, because letting go early would
/// leave a copy nothing will ever remove.
/// </para>
/// </remarks>
[TestClass]
public sealed class SnapshotDeletionPlanTests
{
    private const string Writer = "11111111111111111111111111111111";
    private const ulong Day = 24 * 3_600_000UL;

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ulong NowMs = (ulong)Now.ToUnixTimeMilliseconds();

    [TestMethod]
    public void Select_ARequestedSnapshot_ExpiresWhateverTheRulesKeep()
    {
        // The newest capture, kept today by the daily window and by the floor.
        var history = Daily(first: 1, count: 4, from: Now.AddDays(-4));
        var requested = history[^1] with { DeletionRequest = 9 };

        var selection = RetentionPlanner.Select(
            [.. history[..^1], requested],
            new RetentionConfiguration { KeepDaily = 30, KeepWeekly = 8, KeepMonthly = 12, MinGenerations = 3 },
            Now);

        Assert.AreEqual(requested, Assert.ContainsSingle(selection.Expire));
        Assert.AreEqual(requested, Assert.ContainsSingle(selection.Requested));
        Assert.IsFalse(selection.Keep.Any(keep => keep.Snapshot.SnapshotId == requested.SnapshotId));
    }

    [TestMethod]
    public void Select_ARequestedSnapshot_HoldsNoFloorPlace()
    {
        // A floor of two over three captures, the newest requested: the floor
        // reaches past it, and both older captures stay.
        var history = Daily(first: 1, count: 3, from: Now.AddDays(-300));
        var requested = history[^1] with { DeletionRequest = 9 };

        var selection = RetentionPlanner.Select(
            [.. history[..^1], requested], new RetentionConfiguration { MinGenerations = 2 }, Now);

        Assert.HasCount(2, selection.Keep);
        Assert.IsTrue(selection.Keep.All(keep => keep.Reasons.Contains("min-generations")));
        Assert.AreEqual(requested, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void Select_ARequestedSnapshot_DoesNotRepresentItsDay()
    {
        // Two captures on one day, the later requested: the earlier one is the
        // day's representative now, rather than expiring as the older of two.
        var earlier = Published(1, Now.AddDays(-1).AddHours(-3));
        var later = Published(2, Now.AddDays(-1)) with { DeletionRequest = 9 };

        var selection = RetentionPlanner.Select(
            [earlier, later], new RetentionConfiguration { KeepDaily = 7 }, Now);

        var kept = Assert.ContainsSingle(selection.Keep);
        Assert.AreEqual(earlier, kept.Snapshot);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"daily {Now.AddDays(-1):yyyy-MM-dd}"), kept.Reasons);
        Assert.AreEqual(later, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void Select_NoRuleConfigured_ExpiresTheRequestedSnapshotAndNothingElse()
    {
        // No rule keeps everything, and a request is the one thing that
        // overrides that (ADR-0078's deletion verb).
        var history = Daily(first: 1, count: 3, from: Now.AddDays(-3));
        var requested = history[1] with { DeletionRequest = 9 };

        var selection = RetentionPlanner.Select(
            [history[0], requested, history[2]], new RetentionConfiguration(), Now);

        Assert.HasCount(2, selection.Keep);
        Assert.IsTrue(selection.Keep.All(keep => keep.Reasons.Contains("no rule configured")));
        Assert.AreEqual(requested, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void Select_ARequestedSnapshotWithAnImplausibleTime_IsExpiredAndNotListedAsImplausible()
    {
        // A capture the clock misdated is kept by its flag for ever (FR-GC-012),
        // until a person asks for it to go.
        var history = Daily(first: 1, count: 5, from: Now.AddDays(-6));
        var misdated = Published(6, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)) with
        {
            DeletionRequest = 9,
        };

        var selection = RetentionPlanner.Select(
            [.. history, misdated], new RetentionConfiguration { KeepDaily = 30 }, Now, TimeSpan.FromDays(1));

        Assert.AreEqual(misdated, Assert.ContainsSingle(selection.Expire));
        Assert.IsEmpty(selection.Implausible);
    }

    [TestMethod]
    public void Select_ARequestedSnapshot_IsNotPartOfTheYardstickTheOthersAreJudgedBy()
    {
        // Two captures, three taken while the clock read 2001, then one after
        // it was put right. Counted with the third misdated capture, the 2001
        // run is the longest, and the two real captures before it read as the
        // outliers. That third capture is the one requested, and the others are
        // judged as they will be once it has gone: the 2001 pair is flagged.
        var history = Daily(first: 1, count: 2, from: Now.AddDays(-10));
        var misdated = Daily(first: 3, count: 2, from: new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var requested = Published(5, new DateTimeOffset(2001, 1, 3, 0, 0, 0, TimeSpan.Zero)) with
        {
            DeletionRequest = 9,
        };
        var restored = Published(6, Now.AddDays(-1));

        var selection = RetentionPlanner.Select(
            [.. history, .. misdated, requested, restored], new RetentionConfiguration { KeepDaily = 30 }, Now,
            TimeSpan.FromDays(1));

        CollectionAssert.AreEquivalent(
            misdated, selection.Implausible.Select(implausible => implausible.Snapshot).ToList());
        Assert.IsTrue(selection.Implausible.All(implausible => implausible.Direction == ImplausibleCaptureTime.Behind));
        Assert.AreEqual(requested, Assert.ContainsSingle(selection.Expire));
    }

    [TestMethod]
    public void Apply_ARequestedSnapshot_IsHeldUntilEveryDestinationHasConvergedSinceTheRequest()
    {
        var requested = Published(5, Now.AddDays(-2)) with { DeletionRequest = 12 };

        var result = ReplicationGate.Apply(
            [requested], ["vault", "friend"],
            name => Converged(name == "vault" ? 12UL : 11UL),
            deferralDays: null, NowMs);

        Assert.IsEmpty(result.Expirable);
        var held = Assert.ContainsSingle(result.Held);
        Assert.IsTrue(held.DeletionPending);
        Assert.AreEqual("friend", Assert.ContainsSingle(held.AwaitingDestinations));
    }

    [TestMethod]
    public void Apply_ARequestedSnapshotEveryDestinationHasConvergedPast_IsReleased()
    {
        var requested = Published(5, Now.AddDays(-2)) with { DeletionRequest = 12 };

        var result = ReplicationGate.Apply(
            [requested], ["vault", "friend"], _ => Converged(13), deferralDays: null, NowMs);

        Assert.AreEqual(requested, Assert.ContainsSingle(result.Expirable));
        Assert.IsEmpty(result.Held);
    }

    [TestMethod]
    public void Apply_ARequestedSnapshot_IsHeldForADestinationTheLedgerHasNoRowFor()
    {
        // No row is not proof of no copy. A destination renamed starts a new
        // row under its new name, and a ledger that could not be read starts
        // empty, while the drive itself still holds what it was given. Only a
        // converge since the request says it has let the snapshot go.
        var requested = Published(5, Now.AddDays(-2)) with { DeletionRequest = 12 };

        var result = ReplicationGate.Apply(
            [requested], ["vault", "renamed-drive"],
            name => name == "vault" ? Converged(12) : null,
            deferralDays: null, NowMs);

        Assert.IsEmpty(result.Expirable);
        var held = Assert.ContainsSingle(result.Held);
        Assert.IsTrue(held.DeletionPending);
        Assert.AreEqual("renamed-drive", Assert.ContainsSingle(held.AwaitingDestinations));
    }

    [TestMethod]
    public void Apply_ARequestedSnapshot_IsHeldForADestinationThatNeverReceivedIt()
    {
        // Synced before the snapshot was published, then a pass that failed
        // part-way: some of the snapshot may be there, and once staging lets go
        // nothing will ever remove it.
        var requested = Published(5, Now.AddDays(-2)) with { DeletionRequest = 12 };
        var cutShort = Converged(0) with
        {
            SyncedSequence = 4,
            State = DestinationSyncState.Failed,
            ConsecutiveFailures = 1,
        };

        var result = ReplicationGate.Apply(
            [requested], ["vault"], _ => cutShort, deferralDays: null, NowMs);

        var held = Assert.ContainsSingle(result.Held);
        Assert.IsTrue(held.DeletionPending);
        Assert.AreEqual("vault", Assert.ContainsSingle(held.AwaitingDestinations));
    }

    [TestMethod]
    public void Apply_ARequestedSnapshot_IsHeldWhereTheDestinationsOwnPolicyAlreadyDropsIt()
    {
        // The keep-awareness that lets ordinary expiry go past a narrow
        // destination does not apply: that destination may still hold the
        // snapshot from before its policy dropped it, until it converges.
        var requested = Published(5, Now.AddDays(-2)) with { DeletionRequest = 12 };

        var result = ReplicationGate.Apply(
            [requested], ["narrow"], _ => Converged(3),
            keptBy: static (_, _) => false, deferralDays: null, NowMs);

        Assert.IsTrue(Assert.ContainsSingle(result.Held).DeletionPending);
    }

    [TestMethod]
    public void Apply_ARequestedSnapshotHeldPastTheDeferralBound_IsStillHeld()
    {
        // Ordinary expiry has a bound past which the hold becomes a warning.
        // A request has none: the snapshot is going, and what the hold protects
        // is the destination that would otherwise keep it for ever.
        var requested = Published(5, Now.AddDays(-400)) with { DeletionRequest = 12 };
        var lastSeen = Converged(3) with { LastSuccessAt = NowMs - 200 * Day };

        var result = ReplicationGate.Apply(
            [requested], ["vault"], _ => lastSeen, deferralDays: 30, NowMs);

        var held = Assert.ContainsSingle(result.Held);
        Assert.IsTrue(held.DeletionPending);
        Assert.IsFalse(held.DeferralExceeded);
    }

    [TestMethod]
    public void Apply_AnOrdinaryExpiry_IsNotADeletionRequest()
    {
        var expired = Published(5, Now.AddDays(-40));

        var result = ReplicationGate.Apply(
            [expired], ["vault"], _ => Converged(0) with { SyncedSequence = 3 }, deferralDays: null, NowMs);

        Assert.IsFalse(Assert.ContainsSingle(result.Held).DeletionPending);
    }

    [TestMethod]
    public void WouldLeaveNothing_TheLastCompleteSnapshot_IsGuardedWhilePartialOnesRemain()
    {
        // A partial capture is a real backup but cannot stand in for a whole
        // one, as the floor says (architecture 07 §2).
        var complete = Published(1, Now.AddDays(-3));
        var partial = Published(2, Now.AddDays(-2)) with { CaptureStatus = 2 };

        Assert.IsTrue(SnapshotDeletion.WouldLeaveNothing([complete, partial], [complete.SnapshotId]));
        Assert.IsFalse(SnapshotDeletion.WouldLeaveNothing([complete, partial], [partial.SnapshotId]));
    }

    [TestMethod]
    public void WouldLeaveNothing_ASetWithNoCompleteSnapshot_KeepsItsLastOne()
    {
        // A source that always has a file it cannot read makes every capture
        // partial. Counting only complete snapshots would guard none of them.
        var first = Published(1, Now.AddDays(-3)) with { CaptureStatus = 2 };
        var second = Published(2, Now.AddDays(-2)) with { CaptureStatus = 2 };

        Assert.IsTrue(SnapshotDeletion.WouldLeaveNothing([first, second], [first.SnapshotId, second.SnapshotId]));
        Assert.IsFalse(SnapshotDeletion.WouldLeaveNothing([first, second], [first.SnapshotId]));
    }

    [TestMethod]
    public void WouldLeaveNothing_ASnapshotAlreadyRequested_CountsAsGone()
    {
        var first = Published(1, Now.AddDays(-3));
        var second = Published(2, Now.AddDays(-2));
        var partial = Published(3, Now.AddDays(-1)) with { CaptureStatus = 2, DeletionRequest = 12 };

        Assert.IsTrue(SnapshotDeletion.WouldLeaveNothing(
            [first with { DeletionRequest = 12 }, second, partial], [second.SnapshotId]));
        Assert.IsFalse(SnapshotDeletion.WouldLeaveNothing([first, second, partial], [second.SnapshotId]));
    }

    [TestMethod]
    public void WouldLeaveNothing_IdsAreMatchedWhateverTheirCase()
    {
        // The command matches the ids a person typed without regard to case,
        // so the guard must too, or an upper-case id would slip past it.
        var only = Published(1, Now.AddDays(-1)) with { SnapshotId = "5ac0ffee" };

        Assert.IsTrue(SnapshotDeletion.WouldLeaveNothing([only], ["5AC0FFEE"]));
    }

    private static DestinationSyncRecord Converged(ulong convergedSequence) => new()
    {
        SetId = new string('a', 32),
        Destination = "vault",
        State = DestinationSyncState.InSync,
        LastAttemptAt = NowMs - Day,
        LastSuccessAt = NowMs - Day,
        SyncedSequence = 20,
        ConvergedSequence = convergedSequence,
    };

    private static SnapshotFact Published(ulong sequence, DateTimeOffset capturedAt) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"{Writer[..4]}-{sequence:d4}"),
            (ulong)capturedAt.ToUnixTimeMilliseconds(),
            sequence,
            1,
            Writer);

    /// <summary>One capture a day, published in that order under consecutive sequences.</summary>
    private static List<SnapshotFact> Daily(ulong first, int count, DateTimeOffset from) =>
        [.. Enumerable.Range(0, count).Select(day => Published(first + (ulong)day, from.AddDays(day)))];
}
