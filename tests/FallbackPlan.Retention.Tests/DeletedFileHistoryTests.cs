using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A file deleted from the sources stays restorable for the duration the
/// policy declares (FR-GC-014, ADR-0094, architecture 07 §2): the newest
/// snapshot still holding it is kept until that long after the first
/// snapshot without it, and so is that first snapshot, which dates the
/// deletion.
/// </summary>
/// <remarks>
/// <para>
/// The rule reads adjacent snapshots in the order the other rules read them:
/// the plausible ones nobody asked to delete, newest first. Whether a pair
/// lost a path is the survey's answer (<see cref="DeletedFileSurveyTests"/>),
/// handed in here as <see cref="DeletedFiles"/>. A pair it has no answer for
/// counts as one that lost something, because the rule only ever keeps more.
/// </para>
/// <para>
/// The first snapshot without the file is kept for the same reason the
/// holder is. Were it let go, the holder's next neighbour would be a later
/// snapshot, and the deletion would read as later each time a neighbour
/// expired, so the holder would never go.
/// </para>
/// </remarks>
[TestClass]
public sealed class DeletedFileHistoryTests
{
    private const string Writer = "11111111111111111111111111111111";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Select_AFileDeletedInsideTheDuration_KeepsTheLastSnapshotHoldingIt_AndTheFirstWithout()
    {
        // Under the floor alone only the newest would survive. The file went
        // between the twenty-day-old snapshot and the ten-day-old one.
        var holder = At(Now.AddDays(-20));
        var dater = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));

        var selection = RetentionPlanner.Select(
            [holder, dater, newest],
            new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 },
            Now,
            deletedFiles: DeletedFiles.NoneBetween([new AdjacentSnapshots(dater, newest)]));

        Assert.IsEmpty(selection.Expire);
        Assert.HasCount(3, selection.Keep);
        CollectionAssert.AreEqual(new[] { "deleted files until 2026-10-21" }, ReasonsFor(selection, holder));
        CollectionAssert.AreEqual(
            new[] { "deleted files until 2026-10-21 (first without them)" }, ReasonsFor(selection, dater));
        CollectionAssert.AreEqual(new[] { "min-generations" }, ReasonsFor(selection, newest));
    }

    [TestMethod]
    public void Select_TheReasons_AreTheReportsWords_InEveryCulture()
    {
        // The date is the calendar retention keeps everywhere else, whatever
        // the process culture's own calendar says.
        var until = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        foreach (var culture in new[] { "th-TH", "ar-SA", "tr-TR" })
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                Assert.AreEqual("deleted files until 2026-10-21", RetentionPlanner.DeletedFilesReason(until), culture);
                Assert.AreEqual(
                    "deleted files until 2026-10-21 (first without them)",
                    RetentionPlanner.FirstWithoutDeletedFilesReason(until),
                    culture);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    [TestMethod]
    public void Select_OnceTheDurationHasRun_TheRulesAloneDecide()
    {
        var holder = At(Now.AddDays(-20));
        var dater = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));
        var policy = new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 10 };
        var deleted = DeletedFiles.NoneBetween([new AdjacentSnapshots(dater, newest)]);

        // Ten days after the first snapshot without the file is the moment
        // it stops being kept; a millisecond before, it is still kept.
        var justBefore = RetentionPlanner.Select(
            [holder, dater, newest], policy, Now.AddMilliseconds(-1), deletedFiles: deleted);
        Assert.IsEmpty(justBefore.Expire);

        var atTheEnd = RetentionPlanner.Select([holder, dater, newest], policy, Now, deletedFiles: deleted);
        Assert.AreEqual(newest, Assert.ContainsSingle(atTheEnd.Keep).Snapshot);
        CollectionAssert.AreEquivalent(new[] { holder, dater }, atTheEnd.Expire.ToArray());
    }

    [TestMethod]
    public void Select_TheFirstSnapshotWithoutAFile_IsKept_SoTheDeletionDateCannotDrift()
    {
        // The file went between the holder and the dater. A pass at Now
        // expires the snapshot between the dater and the newest; the passes
        // after it see the dater and the newest as neighbours.
        var holder = At(Now.AddDays(-20));
        var dater = At(Now.AddDays(-10));
        var between = At(Now.AddDays(-5));
        var newest = At(Now.AddDays(-1));
        var policy = new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 15 };
        var deleted = DeletedFiles.NoneBetween(
        [
            new AdjacentSnapshots(dater, between),
            new AdjacentSnapshots(between, newest),
            new AdjacentSnapshots(dater, newest),
        ]);

        var first = RetentionPlanner.Select([holder, dater, between, newest], policy, Now, deletedFiles: deleted);
        Assert.AreEqual(between, Assert.ContainsSingle(first.Expire));

        // Four days on, over what the first pass left: the holder is still
        // kept by the same deletion, dated where it was.
        var survivors = new[] { holder, dater, newest };
        var second = RetentionPlanner.Select(survivors, policy, Now.AddDays(4), deletedFiles: deleted);
        Assert.IsEmpty(second.Expire);
        CollectionAssert.AreEqual(new[] { "deleted files until 2026-10-06" }, ReasonsFor(second, holder));

        // And it goes when the duration since the first snapshot without the
        // file has run, not the duration since some later neighbour.
        var third = RetentionPlanner.Select(survivors, policy, Now.AddDays(5), deletedFiles: deleted);
        CollectionAssert.AreEquivalent(new[] { holder, dater }, third.Expire.ToArray());
    }

    [TestMethod]
    public void Select_ASnapshotEitherSideOfTwoDeletions_CarriesBothReasons()
    {
        // The middle snapshot is the first without one file and the last with
        // another: it is kept for whichever deletion runs longer, and says so.
        var oldest = At(Now.AddDays(-20));
        var middle = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));

        var selection = RetentionPlanner.Select(
            [oldest, middle, newest],
            new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 },
            Now,
            deletedFiles: DeletedFiles.Unknown);

        CollectionAssert.AreEqual(
            new[] { "deleted files until 2026-10-29", "deleted files until 2026-10-21 (first without them)" },
            ReasonsFor(selection, middle));
        CollectionAssert.AreEqual(
            new[] { "min-generations", "deleted files until 2026-10-29 (first without them)" },
            ReasonsFor(selection, newest));
    }

    [TestMethod]
    public void Select_ADeletedFileDurationAlone_DeletesNothing_AndAddsNothing()
    {
        // The duration only ever keeps more, so a policy with no rule that
        // expires anything has nothing for it to add: everything is kept,
        // and the report says why in the words it always has.
        var snapshots = Days(400, 200, 10, 1);

        var selection = RetentionPlanner.Select(
            snapshots, new RetentionConfiguration { KeepDeletedDays = 30 }, Now, deletedFiles: DeletedFiles.Unknown);

        Assert.IsEmpty(selection.Expire);
        Assert.HasCount(4, selection.Keep);
        Assert.IsTrue(
            selection.Keep.All(keep => keep.Reasons.SequenceEqual(["no rule configured"])),
            string.Join("; ", selection.Keep.SelectMany(keep => keep.Reasons)));
        Assert.IsFalse(DestinationConvergence.HasRules(new RetentionConfiguration { KeepDeletedDays = 30 }));
    }

    [TestMethod]
    public void Select_WithNoAnswerAboutAPair_CountsItAsADeletion()
    {
        // Unknown is the reading that keeps more: a pair the survey could not
        // compare, or a caller that compared nothing, keeps both snapshots
        // for the duration.
        var older = At(Now.AddDays(-20));
        var newer = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));
        var policy = new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 };

        Assert.IsEmpty(RetentionPlanner.Select([older, newer, newest], policy, Now, deletedFiles: DeletedFiles.Unknown).Expire);
        Assert.IsEmpty(RetentionPlanner.Select([older, newer, newest], policy, Now).Expire);

        Assert.IsTrue(DeletedFiles.Unknown.Between(older, newer));
        var answered = DeletedFiles.NoneBetween([new AdjacentSnapshots(older, newer)]);
        Assert.IsFalse(answered.Between(older, newer));
        Assert.IsTrue(answered.Between(newer, newest), "a pair nobody compared is not one known to have lost nothing");
    }

    [TestMethod]
    public void Select_NoFileDeletedAnywhere_KeepsNothingExtra()
    {
        var older = At(Now.AddDays(-20));
        var newer = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));

        var selection = RetentionPlanner.Select(
            [older, newer, newest],
            new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 },
            Now,
            deletedFiles: DeletedFiles.NoneBetween(
                [new AdjacentSnapshots(older, newer), new AdjacentSnapshots(newer, newest)]));

        Assert.AreEqual(newest, Assert.ContainsSingle(selection.Keep).Snapshot);
        Assert.HasCount(2, selection.Expire);
    }

    [TestMethod]
    public void Select_APersonsDeletion_OutranksTheDeletedFileRule()
    {
        // The holder was asked to go (FR-GC-013), so it goes, and takes no
        // part in the order: its neighbours are judged as they will be once
        // it has gone, and they lost nothing between them.
        var older = At(Now.AddDays(-30));
        var holder = At(Now.AddDays(-20)) with { DeletionRequest = 4 };
        var dater = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-2));

        var selection = RetentionPlanner.Select(
            [older, holder, dater, newest],
            new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 },
            Now,
            deletedFiles: DeletedFiles.NoneBetween(
                [new AdjacentSnapshots(older, dater), new AdjacentSnapshots(dater, newest)]));

        Assert.AreEqual(holder, Assert.ContainsSingle(selection.Requested));
        Assert.Contains(holder, selection.Expire);
        Assert.IsFalse(selection.Keep.Any(keep => keep.Snapshot.SnapshotId == holder.SnapshotId));
    }

    [TestMethod]
    public void DeletedFilePairs_AreTheNeighboursTheRuleReads_WithinTheDuration_NewestFirst()
    {
        // Publication order is the clock's alibi: sequence 3 was dated a month
        // ahead, so its time cannot place it, and it is kept by its flag
        // instead. Sequence 5 was asked to go. Neither is anyone's neighbour.
        var s1 = Published(1, Now.AddDays(-40));
        var s2 = Published(2, Now.AddDays(-30));
        var ahead = Published(3, Now.AddDays(30));
        var s4 = Published(4, Now.AddDays(-10));
        var requested = Published(5, Now.AddDays(-5)) with { DeletionRequest = 5 };
        var s6 = Published(6, Now.AddDays(-1));

        var pairs = RetentionPlanner.DeletedFilePairs([s1, s2, ahead, s4, requested, s6], 25, Now);

        // The pair (s1, s2) is dated by s2, thirty days ago: outside the
        // twenty-five, so nothing it could keep is the rule's to keep.
        CollectionAssert.AreEqual(
            new[] { new AdjacentSnapshots(s4, s6), new AdjacentSnapshots(s2, s4) },
            pairs.ToArray());
    }

    [TestMethod]
    public void KeepSetsByDestination_AnOverride_KeepsDeletedFilesForItsOwnDuration()
    {
        // FR-GC-010: each destination keeps what its own policy keeps. The
        // file went between the holder and the dater ten days ago.
        var holder = At(Now.AddDays(-20));
        var dater = At(Now.AddDays(-10));
        var newest = At(Now.AddDays(-1));
        var deleted = DeletedFiles.NoneBetween([new AdjacentSnapshots(dater, newest)]);

        var kept = DestinationConvergence.KeepSetsByDestination(
            [holder, dater, newest],
            [
                new SetDestinationReference
                {
                    Ref = "long", Retention = new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 30 },
                },
                new SetDestinationReference
                {
                    Ref = "short", Retention = new RetentionConfiguration { MinGenerations = 1, KeepDeletedDays = 5 },
                },
                new SetDestinationReference { Ref = "set" },
            ],
            new RetentionConfiguration { MinGenerations = 1 },
            Now,
            deletedFiles: deleted);

        CollectionAssert.AreEquivalent(
            new[] { holder.SnapshotId, dater.SnapshotId, newest.SnapshotId }, kept["long"]!.ToArray());
        CollectionAssert.AreEquivalent(new[] { newest.SnapshotId }, kept["short"]!.ToArray());
        CollectionAssert.AreEquivalent(new[] { newest.SnapshotId }, kept["set"]!.ToArray());
    }

    private static string[] ReasonsFor(RetentionSelection selection, SnapshotFact snapshot) =>
        [.. selection.Keep.Single(keep => keep.Snapshot.SnapshotId == snapshot.SnapshotId).Reasons];

    private static SnapshotFact At(DateTimeOffset capturedAt) =>
        new(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, 'f'),
            (ulong)capturedAt.ToUnixTimeMilliseconds());

    private static SnapshotFact Published(ulong sequence, DateTimeOffset capturedAt) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"{Writer[..4]}-{sequence:d4}"),
            (ulong)capturedAt.ToUnixTimeMilliseconds(),
            sequence,
            1,
            Writer);

    /// <summary>Snapshots captured the given number of days ago, oldest first.</summary>
    private static IReadOnlyList<SnapshotFact> Days(params int[] daysAgo) =>
        [.. daysAgo.OrderByDescending(days => days).Select(days => At(Now.AddDays(-days)))];
}
