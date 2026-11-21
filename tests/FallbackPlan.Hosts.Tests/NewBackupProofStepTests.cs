using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Where a destination's proof of a new backup stands after a pass
/// (FR-VER-002, ADR-0199): proven through the backup once every blob it needs
/// beyond the last proven backup has been proven there, and otherwise under
/// way, resumed after the last blob proven without a gap before it. Where
/// it is under way, it says how many of the backup's blobs it has proven and
/// how many are left, which the status reports (FR-VER-003, ADR-0199
/// Amendment 2).
/// </summary>
[TestClass]
public sealed class NewBackupProofStepTests
{
    private const string Older = "snapshots/d/s/older";
    private const string Newest = "snapshots/d/s/newest";

    private static readonly string[] Asked = ["blobs/data/a/x1", "blobs/data/a/x2", "blobs/meta/b/x3"];

    [TestMethod]
    public void EveryBlobAskedProven_WithNoneRemaining_ProvesTheDestinationThroughTheBackup()
    {
        var step = NewBackupProof.Advance(Newest, Older, proving: null, cursor: null, Asked, Answered(Asked), remaining: 0);

        Assert.AreEqual(new NewBackupProof.Step(Newest, null, null), step);
    }

    [TestMethod]
    public void EveryBlobAskedProven_WithSomeRemaining_LeavesTheProofUnderWay_AfterTheLastOfThem()
    {
        var step = NewBackupProof.Advance(Newest, Older, proving: null, cursor: null, Asked, Answered(Asked), remaining: 2);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/meta/b/x3", BlobsProven: 3, BlobsLeft: 2), step);
    }

    [TestMethod]
    public void AProofResumed_CountsOnFromTheBlobsItHadProven()
    {
        var step = NewBackupProof.Advance(
            Newest, Older, proving: Newest, cursor: "blobs/data/a/x0", Asked, Answered(Asked), remaining: 2, blobsProven: 4);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/meta/b/x3", BlobsProven: 7, BlobsLeft: 2), step);
    }

    [TestMethod]
    public void ABlobLeftUnproven_StopsTheCursorBeforeIt_ThoughOnesAfterItWereProven()
    {
        // A blob the digest tier's budget left over is neither proven nor
        // failed: the next pass must ask about it again.
        var step = NewBackupProof.Advance(
            Newest, Older, proving: null, cursor: null, Asked, Answered("blobs/data/a/x1", "blobs/meta/b/x3"), remaining: 0);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/data/a/x1", BlobsProven: 1, BlobsLeft: 2), step);
    }

    [TestMethod]
    public void ABlobLeftUnproven_CountsTheBlobsLeftAfterIt_AndThoseNotYetAsked()
    {
        var step = NewBackupProof.Advance(
            Newest, Older, proving: null, cursor: null, Asked, Answered("blobs/data/a/x1"), remaining: 4);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/data/a/x1", BlobsProven: 1, BlobsLeft: 6), step);
    }

    [TestMethod]
    public void AnUncountedProof_WithABlobLeftUnproven_StaysUncounted()
    {
        var step = NewBackupProof.Advance(
            Newest, Older, proving: Newest, cursor: null, Asked, Answered("blobs/data/a/x1"), remaining: 4, blobsProven: null);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/data/a/x1", BlobsProven: null, BlobsLeft: null), step);
    }

    [TestMethod]
    public void NothingProvenThisPass_KeepsTheCursorOfTheSameBackup()
    {
        var step = NewBackupProof.Advance(
            Newest, Older, proving: Newest, cursor: "blobs/data/a/x0", Asked, Answered(), remaining: 0, blobsProven: 5);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/data/a/x0", BlobsProven: 5, BlobsLeft: 3), step);
    }

    [TestMethod]
    public void AProofUnderWayBeforeItWasCounted_StaysUncounted_RatherThanCountingFromNone()
    {
        // A ledger from before the counts kept a proof under way with none:
        // counted from nothing, its whole would read short until it ended.
        var step = NewBackupProof.Advance(
            Newest, Older, proving: Newest, cursor: "blobs/data/a/x0", Asked, Answered(Asked), remaining: 2, blobsProven: null);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, "blobs/meta/b/x3", BlobsProven: null, BlobsLeft: null), step);
    }

    [TestMethod]
    public void ANewerBackup_StartsItsOwnProofFromTheBeginning()
    {
        // The cursor was a place in another backup's blobs; this one's may
        // sort before it.
        var step = NewBackupProof.Advance(
            Newest, Older, proving: Older, cursor: "blobs/meta/z/x9", Asked, Answered(), remaining: 0, blobsProven: 5);

        Assert.AreEqual(new NewBackupProof.Step(Older, Newest, null, BlobsProven: 0, BlobsLeft: 3), step);
    }

    [TestMethod]
    public void ABackupNeedingNothingNew_IsProvenAtOnce()
    {
        var step = NewBackupProof.Advance(Newest, Older, proving: null, cursor: null, [], Answered(), remaining: 0);

        Assert.AreEqual(new NewBackupProof.Step(Newest, null, null), step);
    }

    private static HashSet<string> Answered(params string[] keys) => new(keys, StringComparer.Ordinal);
}
