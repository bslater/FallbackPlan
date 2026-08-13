using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The five-value per-<c>(snapshot, destination)</c> vocabulary (FR-SNP-003),
/// derived from the sync ledger's facts rather than stored beside them — the
/// same rows the replication gate and the status roll-up read.
/// </summary>
[TestClass]
public sealed class SnapshotReplicationTests
{
    private static DestinationSyncRecord Record(
        DestinationSyncState state = DestinationSyncState.InSync,
        ulong synced = 0,
        ulong? verifiedAt = null,
        ulong verifiedSequence = 0) => new()
    {
        SetId = new string('a', 32),
        Destination = "vault",
        State = state,
        LastAttemptAt = 1_722_600_000_000,
        SyncedSequence = synced,
        VerifiedAt = verifiedAt,
        VerifiedSequence = verifiedSequence,
    };

    [TestMethod]
    public void Derive_NoSyncHasEverRun_IsPendingUntilOneStarts()
    {
        Assert.AreEqual(
            SnapshotReplicationState.Pending,
            SnapshotReplication.Derive(1, record: null, syncActive: false));
        Assert.AreEqual(
            SnapshotReplicationState.Replicating,
            SnapshotReplication.Derive(1, record: null, syncActive: true));
    }

    [TestMethod]
    public void Derive_TheSyncedSequenceDecides_DurableAgainstPending()
    {
        // The gate's currency (FR-GC-009): delivered means the snapshot's
        // publication is at or before what the last successful sync carried.
        var record = Record(synced: 3);
        Assert.AreEqual(SnapshotReplicationState.Durable, SnapshotReplication.Derive(3, record, syncActive: false));
        Assert.AreEqual(SnapshotReplicationState.Pending, SnapshotReplication.Derive(4, record, syncActive: false));
        Assert.AreEqual(
            SnapshotReplicationState.Replicating, SnapshotReplication.Derive(4, record, syncActive: true));
    }

    [TestMethod]
    public void Derive_AVerificationStamp_UpgradesExactlyWhatItCovers()
    {
        var record = Record(synced: 5, verifiedAt: 1_722_600_000_000, verifiedSequence: 3);
        Assert.AreEqual(SnapshotReplicationState.Verified, SnapshotReplication.Derive(3, record, syncActive: false));
        Assert.AreEqual(SnapshotReplicationState.Durable, SnapshotReplication.Derive(5, record, syncActive: false));
    }

    [TestMethod]
    public void Derive_AFailingDestination_DegradesEverySnapshotThere()
    {
        // Reached-and-wrong — a refusal, a failed verification — makes the
        // whole copy suspect, delivered snapshots included.
        var record = Record(DestinationSyncState.Failed, synced: 5, verifiedAt: 1, verifiedSequence: 5);
        Assert.AreEqual(SnapshotReplicationState.Degraded, SnapshotReplication.Derive(3, record, syncActive: false));
    }

    [TestMethod]
    public void Derive_AnUnreachableDestination_DegradesOnlyWhatHasNotCrossed()
    {
        // An unplugged drive still holds what it holds.
        var record = Record(DestinationSyncState.Unavailable, synced: 3);
        Assert.AreEqual(SnapshotReplicationState.Durable, SnapshotReplication.Derive(2, record, syncActive: false));
        Assert.AreEqual(SnapshotReplicationState.Degraded, SnapshotReplication.Derive(4, record, syncActive: false));
    }

    [TestMethod]
    public void Label_SpeaksTheRequirementsVocabulary()
    {
        Assert.AreEqual("pending", SnapshotReplication.Label(SnapshotReplicationState.Pending));
        Assert.AreEqual("replicating", SnapshotReplication.Label(SnapshotReplicationState.Replicating));
        Assert.AreEqual("durable", SnapshotReplication.Label(SnapshotReplicationState.Durable));
        Assert.AreEqual("verified", SnapshotReplication.Label(SnapshotReplicationState.Verified));
        Assert.AreEqual("degraded", SnapshotReplication.Label(SnapshotReplicationState.Degraded));
    }
}
