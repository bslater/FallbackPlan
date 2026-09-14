namespace FallbackPlan.Application;

/// <summary>
/// What a replication pass should do for one <c>(set, destination)</c> pair
/// before it reads anything (ADR-0056).
/// </summary>
public enum SyncScope
{
    /// <summary>
    /// Nothing: the destination provably holds everything published, its
    /// keep-set has not moved, and its inventory was read through recently
    /// enough. The pass costs the sequence read that decided this.
    /// </summary>
    Skip,

    /// <summary>
    /// Carry what is new over the named dependency phases, each under its own
    /// prefix. The catch-all sweep is left to a reconciling pass.
    /// </summary>
    Incremental,

    /// <summary>
    /// Read both inventories through, catch-all sweep included: the only pass
    /// that can find what the watermark cannot see.
    /// </summary>
    Reconcile,
}

/// <summary>
/// Decides how much of a replication pass to run, from the sync ledger's
/// record and the source's current publication sequence (ADR-0056,
/// NFR-PERF-005).
/// </summary>
/// <remarks>
/// <para>
/// A pure rule, decided before any listing, because the cost this exists to
/// avoid is paid by the act of looking. The ledger's <c>synced_sequence</c>
/// has been recorded since the staging model — "everything published at or
/// before this is at the destination" — and until now nothing on the copy path
/// read it: every pass re-derived that fact by enumerating both stores.
/// </para>
/// <para>
/// <b>Every arm errs towards reading.</b> A skip is a claim that a destination
/// is level made without looking at it, so it is made only from facts that
/// were written down by a pass that did look, and it expires. What the
/// watermark cannot see is everything on the destination's own side — a file
/// deleted out of the replica, a half-finished copy from a build with a bug in
/// it, an object under a prefix no dependency phase names. None of those move
/// a publication sequence and all of them are found by reading the inventories
/// through, which is why <see cref="DefaultIntervalMilliseconds"/> exists and
/// why it is a day rather than a week.
/// </para>
/// </remarks>
public static class ReconciliationGate
{
    /// <summary>
    /// How long a reading-through stays good for: one day.
    /// </summary>
    /// <remarks>
    /// The number is a trade between the cost of a full pass on a large
    /// archive and the time a destination can be quietly wrong. A day bounds
    /// the second without making the first a daily event on any archive small
    /// enough for it to matter. Content damage is not what this catches —
    /// verification and the restore drill do that, on their own cadences, by
    /// reading bytes rather than names.
    /// </remarks>
    public const ulong DefaultIntervalMilliseconds = 24 * 60 * 60 * 1000;

    /// <summary>Decides what this pass should do.</summary>
    /// <param name="record">The pair's ledger row, or null when there is none.</param>
    /// <param name="sourceSequence">The source archive's highest publication sequence now.</param>
    /// <param name="keepFingerprint">
    /// A stable rendering of this destination's keep-set, or null when its
    /// policy keeps everything. A keep-set moves with the clock rather than
    /// with publication, so a pair with nothing published can still owe its
    /// destination a deletion.
    /// </param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="intervalMilliseconds">How long a reading-through stays good for.</param>
    /// <param name="mustReadThrough">
    /// The caller knows the source holds history the watermark cannot speak
    /// for. The case is a direct-ship set still carrying the staging archive
    /// it migrated from ([ADR-0046](../../docs/adr/0046-direct-to-destination-publication.md)):
    /// its runs ship their own objects and record success for them, which says
    /// nothing whatever about the older history only staging holds. Skipping
    /// there would strand exactly the copy the migration is waiting on.
    /// </param>
    /// <returns>How much of the pass to run.</returns>
    public static SyncScope Decide(
        DestinationSyncRecord? record,
        ulong sourceSequence,
        string? keepFingerprint,
        ulong nowUnixMilliseconds,
        ulong intervalMilliseconds = DefaultIntervalMilliseconds,
        bool mustReadThrough = false)
    {
        // No row, no baseline, or a state that is not claiming to be level:
        // there is no recorded looking to stand on.
        if (mustReadThrough
            || record is null
            || record.NeedsFull
            || record.State != DestinationSyncState.InSync
            || record.LastSuccessAt is null)
        {
            return SyncScope.Reconcile;
        }

        // Never read through, or read through so long ago that the reading no
        // longer says anything about now. A stamp in the future is a clock
        // that moved: treated as due rather than as good until it catches up,
        // which for a corrected clock could be days.
        if (record.LastReconciledAt is not { } reconciledAt
            || reconciledAt > nowUnixMilliseconds
            || nowUnixMilliseconds - reconciledAt >= intervalMilliseconds)
        {
            return SyncScope.Reconcile;
        }

        // The source claiming less than the destination was told it holds is
        // not a pair to skip: either the archive was rebuilt underneath the
        // ledger or the ledger is wrong about it, and both want a pass that
        // looks rather than one that assumes.
        if (sourceSequence < record.SyncedSequence)
        {
            return SyncScope.Reconcile;
        }

        // Something published since, or a keep-set that has moved: the named
        // phases carry both, and the catch-all sweep still waits for the
        // reading-through it belongs to.
        return sourceSequence > record.SyncedSequence
            || !string.Equals(keepFingerprint, record.KeepFingerprint, StringComparison.Ordinal)
            ? SyncScope.Incremental
            : SyncScope.Skip;
    }
}
