using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// What a replication pass decides to do before it does anything
/// (NFR-PERF-005, FR-REP-006, [ADR-0056](../../docs/adr/0056-incremental-reconciliation.md)):
/// skip, carry what is new, or read both inventories through.
/// </summary>
/// <remarks>
/// <para>
/// The rule is a pure function of the ledger row, the source's publication
/// sequence and the clock, so it is decided here rather than inside a pass
/// that has already paid for the answer. Every arm errs the same way: when the
/// inputs do not prove the destination is level, the pass reconciles. A wrong
/// skip is invisible until somebody needs the data.
/// </para>
/// <para>
/// This suite does not establish FR-DEST-003 — the back-off that decides
/// whether a pass runs at all is the scheduler's, and this gate only decides
/// what a pass that is already running should do.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReconciliationGateTests
{
    private const ulong Now = 1_800_000_000_000;
    private const string Fingerprint = "a1b2c3d4";

    [TestMethod]
    public void Decide_APairWithNoRowAtAll_Reconciles()
    {
        // Nothing recorded is not the same as nothing owed.
        Assert.AreEqual(
            SyncScope.Reconcile,
            ReconciliationGate.Decide(null, sourceSequence: 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_APairOwedItsFirstFullCopy_Reconciles()
    {
        var record = Level() with { NeedsFull = true };

        Assert.AreEqual(SyncScope.Reconcile, ReconciliationGate.Decide(record, 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_APairThatIsNotInSync_Reconciles()
    {
        // Behind, unavailable or failed: the watermark describes a destination
        // that was level once, and this one is not claiming to be.
        foreach (var state in new[]
        {
            DestinationSyncState.Behind, DestinationSyncState.Unavailable, DestinationSyncState.Failed,
        })
        {
            Assert.AreEqual(
                SyncScope.Reconcile,
                ReconciliationGate.Decide(Level() with { State = state }, 4, Fingerprint, Now),
                $"a {state} pair must not be skipped on the strength of its watermark");
        }
    }

    [TestMethod]
    public void Decide_APairNeverReconciled_Reconciles()
    {
        // A row from before reconciliation was recorded says nothing about
        // when the destination's inventory was last read through, so the first
        // pass after an upgrade reads it through.
        var record = Level() with { LastReconciledAt = null };

        Assert.AreEqual(SyncScope.Reconcile, ReconciliationGate.Decide(record, 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_NothingPublishedAndTheReconciliationIsFresh_Skips()
    {
        // The whole point: the source has published nothing since the
        // destination was last read through, so there is nothing a listing
        // could discover and no reason to pay for one.
        Assert.AreEqual(SyncScope.Skip, ReconciliationGate.Decide(Level(), sourceSequence: 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_ASnapshotPublishedSince_CarriesWhatIsNew()
    {
        Assert.AreEqual(
            SyncScope.Incremental,
            ReconciliationGate.Decide(Level(), sourceSequence: 5, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_TheKeepSetChanged_CarriesWhatIsNew()
    {
        // Retention windows move with the clock, not with publication: a
        // snapshot can age out of this destination's policy while nothing at
        // all is published, and the convergence that drops it is the named
        // phases' work.
        Assert.AreEqual(
            SyncScope.Incremental,
            ReconciliationGate.Decide(Level(), sourceSequence: 4, "d4c3b2a1", Now));
    }

    [TestMethod]
    public void Decide_TheReconciliationIntervalElapsed_ReadsBothInventoriesThrough()
    {
        // The watermark cannot see the destination's side. Somebody deleting a
        // file out of the replica, a half-finished copy from a build with a
        // bug in it, a stray under a prefix no phase names — none of them move
        // a publication sequence, and all of them are found by reading the
        // inventories through. That is why the skip has a shelf life.
        var record = Level() with { LastReconciledAt = Now - ReconciliationGate.DefaultIntervalMilliseconds };

        Assert.AreEqual(SyncScope.Reconcile, ReconciliationGate.Decide(record, 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_TheCallerKnowsTheSourceHoldsMore_Reconciles()
    {
        // A direct-ship set that migrated keeps its old staging archive until
        // retirement, and its runs record success for the objects they ship —
        // which says nothing about the history only staging holds. The pair
        // looks level by every field on the row and is not.
        Assert.AreEqual(
            SyncScope.Reconcile,
            ReconciliationGate.Decide(
                Level(), sourceSequence: 4, Fingerprint, Now,
                ReconciliationGate.DefaultIntervalMilliseconds, mustReadThrough: true));
    }

    [TestMethod]
    public void Decide_AClockThatWentBackwards_Reconciles()
    {
        // A stamp in the future is a clock that moved, and "not due yet" would
        // then hold until it caught up — which for a manually corrected clock
        // can be days.
        var record = Level() with { LastReconciledAt = Now + 60_000 };

        Assert.AreEqual(SyncScope.Reconcile, ReconciliationGate.Decide(record, 4, Fingerprint, Now));
    }

    [TestMethod]
    public void Decide_ASequenceBehindTheDestinations_Reconciles()
    {
        // The source claiming less than the destination already holds is not a
        // pair to skip: either the archive was rebuilt or the ledger is wrong
        // about it, and both want a pass that looks.
        Assert.AreEqual(
            SyncScope.Reconcile,
            ReconciliationGate.Decide(Level(), sourceSequence: 3, Fingerprint, Now));
    }

    /// <summary>A pair the last pass left level, read through an hour ago.</summary>
    private static DestinationSyncRecord Level() => new()
    {
        SetId = "set-a",
        Destination = "vault",
        State = DestinationSyncState.InSync,
        LastAttemptAt = Now - 3_600_000,
        LastSuccessAt = Now - 3_600_000,
        SyncedSequence = 4,
        LastReconciledAt = Now - 3_600_000,
        KeepFingerprint = Fingerprint,
    };
}
