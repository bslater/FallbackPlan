namespace FallbackPlan.Application;

/// <summary>
/// One <c>(snapshot, destination)</c> pair's replication status
/// (FR-SNP-003): the five-value vocabulary, derived — never stored — from
/// the sync ledger's per-destination facts, so it can never disagree with
/// the gate and status derivations that read the same rows.
/// </summary>
public enum SnapshotReplicationState
{
    /// <summary>Not yet delivered, and nothing is currently trying.</summary>
    Pending = 0,

    /// <summary>A sync for this pair is queued or running now.</summary>
    Replicating = 1,

    /// <summary>Delivered: the snapshot's publication is at or before what the last successful sync carried.</summary>
    Durable = 2,

    /// <summary>Delivered, and a verification pass has since proven sampled bytes at the destination (peer-protocol 04).</summary>
    Verified = 3,

    /// <summary>The pair needs attention: the destination is failing, or cannot receive what it is owed.</summary>
    Degraded = 4,
}

/// <summary>Derives <see cref="SnapshotReplicationState"/> from ledger facts.</summary>
public static class SnapshotReplication
{
    /// <summary>
    /// Where one snapshot stands at one destination.
    /// </summary>
    /// <param name="publicationSequence">The snapshot's publication sequence — the same currency the replication gate speaks (FR-GC-009).</param>
    /// <param name="record">The pair's ledger row; null when no sync has ever been attempted.</param>
    /// <param name="syncActive">Whether a sync for this pair is queued or running now.</param>
    /// <param name="touchesDamage">
    /// Whether this snapshot needs an object the row names as damaged. A
    /// caller that has not traced the damage passes true, and every snapshot
    /// is then counted as needing it (FR-VER-005).
    /// </param>
    /// <remarks>
    /// <para>
    /// A <see cref="DestinationSyncState.Failed"/> row degrades every
    /// snapshot at that destination — reached-and-wrong (a refusal, a failed
    /// verification) makes the whole copy suspect — while
    /// <see cref="DestinationSyncState.Unavailable"/> degrades only what has
    /// not crossed yet: an unplugged drive still holds what it holds.
    /// </para>
    /// <para>
    /// Damage is the exception, because it has a scope. Objects the row names
    /// as damaged degrade the snapshots that need them, whatever else the row
    /// says, and a row failed for that damage alone degrades those snapshots
    /// and no others: the rest of the copy was read and answered, and a
    /// snapshot that needs none of the damaged objects is restorable from it.
    /// </para>
    /// </remarks>
    public static SnapshotReplicationState Derive(
        ulong publicationSequence, DestinationSyncRecord? record, bool syncActive, bool touchesDamage = true)
    {
        if (touchesDamage && record?.DamagedKeys is { Count: > 0 })
        {
            return SnapshotReplicationState.Degraded;
        }

        if (record?.State == DestinationSyncState.Failed && (touchesDamage || !record.DamageOnly))
        {
            return SnapshotReplicationState.Degraded;
        }

        if (record is not null && record.VerifiedAt is not null
            && publicationSequence <= record.VerifiedSequence)
        {
            return SnapshotReplicationState.Verified;
        }

        if (record is not null && record.SyncedSequence > 0 && publicationSequence <= record.SyncedSequence)
        {
            return SnapshotReplicationState.Durable;
        }

        if (syncActive)
        {
            return SnapshotReplicationState.Replicating;
        }

        return record?.State is DestinationSyncState.Unavailable or DestinationSyncState.NotSupported
            ? SnapshotReplicationState.Degraded
            : SnapshotReplicationState.Pending;
    }

    /// <summary>The requirement's own spelling of each state (FR-SNP-003).</summary>
    public static string Label(SnapshotReplicationState state) => state switch
    {
        SnapshotReplicationState.Pending => "pending",
        SnapshotReplicationState.Replicating => "replicating",
        SnapshotReplicationState.Durable => "durable",
        SnapshotReplicationState.Verified => "verified",
        _ => "degraded",
    };
}
