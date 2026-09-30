using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The five-value per-<c>(snapshot, destination)</c> vocabulary (FR-SNP-003),
/// derived from the sync ledger's facts rather than stored beside them — the
/// same rows the replication gate and the status roll-up read. Damage a pair
/// still holds degrades the snapshots that need it, and a pair failed for
/// that damage alone degrades those and no others (FR-VER-005); a caller that
/// has not traced the damage counts every snapshot as needing it.
/// </summary>
[TestClass]
public sealed class SnapshotReplicationTests
{
    private static DestinationSyncRecord Record(
        DestinationSyncState state = DestinationSyncState.InSync,
        ulong synced = 0,
        ulong? verifiedAt = null,
        ulong verifiedSequence = 0,
        IReadOnlyList<string>? damaged = null,
        bool damageOnly = false) => new()
    {
        SetId = new string('a', 32),
        Destination = "vault",
        State = state,
        LastAttemptAt = 1_722_600_000_000,
        SyncedSequence = synced,
        VerifiedAt = verifiedAt,
        VerifiedSequence = verifiedSequence,
        DamagedKeys = damaged,
        DamageOnly = damageOnly,
    };

    private static readonly string[] Rotted = ["blobs/data/abcd/abcdefghijklmnopqrstuvwxyz234567"];

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
    public void Derive_AFailureThatIsTheDamageAlone_DegradesTheSnapshotsItTouches_AndNoOthers()
    {
        // The copy was reached and read, and all that is wrong with it is
        // what the ledger names: a snapshot that needs none of it is still
        // restorable from there, and saying otherwise is a scare with no
        // remedy attached.
        var record = Record(
            DestinationSyncState.Failed, synced: 5, verifiedAt: 1, verifiedSequence: 5, damaged: Rotted, damageOnly: true);

        Assert.AreEqual(
            SnapshotReplicationState.Degraded, SnapshotReplication.Derive(3, record, syncActive: false, touchesDamage: true));
        Assert.AreEqual(
            SnapshotReplicationState.Verified, SnapshotReplication.Derive(3, record, syncActive: false, touchesDamage: false));
    }

    [TestMethod]
    public void Derive_AFailureBesidesTheDamage_StillDegradesEverySnapshot()
    {
        // A refusal or a failed proof says something about the whole copy,
        // and knowing which snapshots the damage touches does not unsay it.
        var record = Record(
            DestinationSyncState.Failed, synced: 5, verifiedAt: 1, verifiedSequence: 5, damaged: Rotted, damageOnly: false);

        Assert.AreEqual(
            SnapshotReplicationState.Degraded, SnapshotReplication.Derive(3, record, syncActive: false, touchesDamage: false));
    }

    [TestMethod]
    public void Derive_DamageThePairStillHolds_DegradesWhatItTouches_WhateverElseTheRowSays()
    {
        // An unplugged drive holds what it holds — including what it holds
        // damaged. Being away does not make those objects restorable.
        var record = Record(DestinationSyncState.Unavailable, synced: 3, damaged: Rotted);

        Assert.AreEqual(
            SnapshotReplicationState.Degraded, SnapshotReplication.Derive(2, record, syncActive: false, touchesDamage: true));
        Assert.AreEqual(
            SnapshotReplicationState.Durable, SnapshotReplication.Derive(2, record, syncActive: false, touchesDamage: false));
    }

    [TestMethod]
    public void Derive_ACallerThatHasNotTracedTheDamage_CountsEverySnapshotTouched()
    {
        var record = Record(
            DestinationSyncState.Failed, synced: 5, verifiedAt: 1, verifiedSequence: 5, damaged: Rotted, damageOnly: true);

        Assert.AreEqual(SnapshotReplicationState.Degraded, SnapshotReplication.Derive(3, record, syncActive: false));
        Assert.AreEqual(
            SnapshotReplicationState.Degraded,
            SnapshotReplication.Derive(3, Record(DestinationSyncState.Unavailable, synced: 3, damaged: Rotted), syncActive: false),
            "damage nobody traced may be anywhere");
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
