using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Where an original-location restore puts a plan back (FR-RST-001,
/// ADR-0041, ADR-0119), as the service and the command line's direct mode
/// both map it. A set of one root takes the whole plan into that root. A set
/// of several has its plan's paths under the roots' labels (ADR-0040), so
/// each label's files go back into its own root without the label, and a
/// label no root carries any more is named and nothing is sliced. The run's
/// receipts are one receipt, its paths under the labels they were asked by.
/// </summary>
[TestClass]
public sealed class RestoreOriginalLocationTests
{
    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x5A, 16)];

    [TestMethod]
    public void ASetOfOneRoot_TakesTheWholePlan_IntoThatRoot()
    {
        var plan = PlanOf("notes.txt", "photos", "photos/beach.jpg");

        var slices = RestoreOriginalLocation.Slice(plan, [new RestoreRoot("/home/ana/docs", "docs")], out var unmapped);

        Assert.IsNull(unmapped);
        var slice = Assert.ContainsSingle(slices!);
        Assert.AreEqual("/home/ana/docs", slice.OutputDirectory);
        Assert.AreEqual(string.Empty, slice.LabelPrefix);
        Assert.AreSame(plan, slice.Plan, "one root restores the plan as it was made");
    }

    [TestMethod]
    public void ASetOfSeveralRoots_PutsEachLabelsFilesBackIntoItsOwnRoot_WithoutTheLabel()
    {
        var plan = PlanOf("docs", "docs/notes.txt", "media", "media/clip.mp4", "media/album/song.ogg") with
        {
            Conflicts = [new RestoreConflict("docs/notes.txt", "too long here")],
            Degradations = [new MetadataDegradation("acl", "not on this platform")],
        };

        var slices = RestoreOriginalLocation.Slice(
            plan, [new RestoreRoot("/srv/docs", "docs"), new RestoreRoot("/srv/media", "media")], out var unmapped);

        Assert.IsNull(unmapped);
        Assert.HasCount(2, slices!);
        var docs = slices!.Single(slice => slice.OutputDirectory == "/srv/docs");
        var media = slices!.Single(slice => slice.OutputDirectory == "/srv/media");
        Assert.AreEqual("docs/", docs.LabelPrefix);
        Assert.AreEqual("media/", media.LabelPrefix);
        CollectionAssert.AreEqual(new[] { "notes.txt" }, docs.Plan.Items.Select(item => item.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { "clip.mp4", "album/song.ogg" }, media.Plan.Items.Select(item => item.Path).ToArray());
        Assert.IsTrue(media.Plan.SnapshotId.Span.SequenceEqual(SnapshotId));
        Assert.AreSame(plan.Conflicts, docs.Plan.Conflicts);
        Assert.AreSame(plan.Degradations, media.Plan.Degradations);
    }

    [TestMethod]
    public void ALabelNoRootCarriesAnyMore_IsNamed_AndNothingIsSliced()
    {
        var plan = PlanOf("docs/notes.txt", "media/clip.mp4");

        var slices = RestoreOriginalLocation.Slice(
            plan, [new RestoreRoot("/srv/docs", "docs"), new RestoreRoot("/srv/archive", "archive")], out var unmapped);

        Assert.IsNull(slices);
        Assert.AreEqual("media", unmapped);
    }

    [TestMethod]
    public void OneRootsReceipt_IsTheRunsReceipt()
    {
        var receipt = ReceiptOf("/home/ana/docs", RestoreOutcome.Complete, 10, 20, "notes.txt");

        Assert.AreSame(receipt, RestoreOriginalLocation.Merge([(receipt, string.Empty)]));
    }

    [TestMethod]
    public void SeveralRootsReceipts_AreOneReceipt_EachPathUnderTheLabelItWasAskedBy()
    {
        var docs = ReceiptOf("/srv/docs", RestoreOutcome.Complete, 10, 30, "notes.txt") with
        {
            Displaced = ["notes.txt"],
        };
        var media = ReceiptOf("/srv/media", RestoreOutcome.Partial, 20, 40, "clip.mp4") with
        {
            Items = [new ReceiptItem { Path = "clip.mp4", Outcome = "restored", Bytes = 5, WrittenAs = "clip (restored 2026-10-27).mp4" }],
        };

        var merged = RestoreOriginalLocation.Merge([(docs, "docs/"), (media, "media/")]);

        CollectionAssert.AreEqual(new[] { "docs/notes.txt", "media/clip.mp4" }, merged.Items.Select(item => item.Path).ToArray());
        Assert.AreEqual("media/clip (restored 2026-10-27).mp4", merged.Items[1].WrittenAs);
        Assert.IsNull(merged.Items[0].WrittenAs);
        CollectionAssert.AreEqual(new[] { "docs/notes.txt" }, merged.Displaced.ToArray());
        Assert.AreEqual("/srv/docs; /srv/media", merged.WrittenTo);
        Assert.AreEqual(RestoreOutcome.Partial, merged.Outcome, "the run is as good as its worst root");
        Assert.AreEqual(10ul, merged.StartedAt);
        Assert.AreEqual(40ul, merged.CompletedAt);
        Assert.AreEqual(docs.SnapshotId, merged.SnapshotId);
        Assert.AreEqual(RestoreReceipt.CurrentSchemaVersion, merged.SchemaVersion);
    }

    private static RestorePlan PlanOf(params string[] paths) => new()
    {
        SnapshotId = SnapshotId,
        Items =
        [
            .. paths.Select(path => new RestorePlanItem(
                path,
                path.Contains('.', StringComparison.Ordinal) ? EntryKind.File : EntryKind.DirectoryPlaceholder,
                default(ObjectId),
                path.Contains('.', StringComparison.Ordinal) ? 5ul : 0ul)),
        ],
        Conflicts = [],
        Degradations = [],
    };

    private static RestoreReceipt ReceiptOf(string writtenTo, RestoreOutcome outcome, ulong started, ulong completed, string path) => new()
    {
        SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
        SnapshotId = Convert.ToHexStringLower(SnapshotId),
        StartedAt = started,
        CompletedAt = completed,
        Items = [new ReceiptItem { Path = path, Outcome = "restored", Bytes = 5 }],
        Displaced = [],
        WrittenTo = writtenTo,
        Outcome = outcome,
    };
}
