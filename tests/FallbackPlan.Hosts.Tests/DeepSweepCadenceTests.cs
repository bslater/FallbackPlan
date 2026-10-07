using FallbackPlan.Agent;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Which destinations the deep sweep reads back in full, and on what cadence
/// (FR-VER-002, FR-VER-008), stated once: the scheduler keeps it and the
/// status matrix reports it (FR-VER-003, contract 1.46). A second copy of the
/// rule is how the two would come to disagree, and a row that says "every
/// seven days" of a destination the scheduler never sweeps is the kind of
/// reassurance a status must not give.
/// </summary>
[TestClass]
public sealed class DeepSweepCadenceTests
{
    [TestMethod]
    public void ALocalPath_IsSweptOnItsStatedIntervalOrTheDefault()
    {
        Assert.AreEqual(ReplicaSweepJob.DefaultIntervalDays, ReplicaSweepJob.ScheduledIntervalDays(Local(intervalDays: null)));
        Assert.AreEqual(3, ReplicaSweepJob.ScheduledIntervalDays(Local(intervalDays: 3)));
    }

    [TestMethod]
    public void APeer_IsSweptOnlyOnACadenceItsOperatorStated()
    {
        Assert.IsNull(ReplicaSweepJob.ScheduledIntervalDays(Peer(intervalDays: null)));
        Assert.AreEqual(30, ReplicaSweepJob.ScheduledIntervalDays(Peer(intervalDays: 30)));
    }

    [TestMethod]
    public void AKindNoSweepReads_IsNeverSwept_WhateverItsDeclarationStates()
    {
        Assert.IsTrue(ReplicaSweepJob.Sweeps(DestinationKind.LocalPath));
        Assert.IsTrue(ReplicaSweepJob.Sweeps(DestinationKind.Peer));
        foreach (var kind in new[] { DestinationKind.S3, DestinationKind.AzureBlob, DestinationKind.Dropbox })
        {
            // An s3 store is read back by sample at every sync and not swept
            // (ADR-0091); the reserved kinds are read by nothing.
            Assert.IsFalse(ReplicaSweepJob.Sweeps(kind), $"nothing reads {kind} back in full");
            Assert.IsNull(
                ReplicaSweepJob.ScheduledIntervalDays(Declared(kind, intervalDays: 7)),
                $"a stated interval does not make {kind} swept");
        }
    }

    [TestMethod]
    public void TheStatusRow_OfAKindNoSweepReads_OrOfAnUndeclaredDestination_ReportsNoSweepAtAll()
    {
        // Not a sweep that has not run: there is nothing to run. The client
        // draws no deep-verify line for it, as it draws none for a service
        // older than 1.46.
        Assert.IsNull(ServiceCommandHandler.DescribeSweep(Declared(DestinationKind.S3, intervalDays: null), ledger: null));
        Assert.IsNull(ServiceCommandHandler.DescribeSweep(destination: null, ledger: null));
    }

    [TestMethod]
    public void TheStatusRow_OfADestinationNeverRead_ReportsASweepThatHasNotRun_WithItsCadence()
    {
        var local = ServiceCommandHandler.DescribeSweep(Local(intervalDays: null), ledger: null);
        Assert.IsNotNull(local);
        Assert.AreEqual(ReplicaSweepJob.DefaultIntervalDays, local.IntervalDays);
        Assert.IsNull(local.CircuitClosedAt);
        Assert.IsNull(local.LastReadAt);
        Assert.AreEqual(0, local.ReadThisCircuit);

        var peer = ServiceCommandHandler.DescribeSweep(Peer(intervalDays: null), ledger: null);
        Assert.IsNotNull(peer, "a person can have a peer read in full, so it has a sweep to report");
        Assert.IsNull(peer.IntervalDays);
    }

    [TestMethod]
    public void TheStatusRow_CarriesTheLedgersFacts()
    {
        var ledger = new DestinationSyncRecord
        {
            SetId = "set",
            Destination = "vault",
            State = DestinationSyncState.InSync,
            LastAttemptAt = 9_000,
            SweptAt = 9_000,
            SweepCompletedAt = 5_000,
            SweptThisCircuit = 4,
            SweepStalls = 2,
            SweepStalledOn = "blobs/data/ab/abcdef",
        };

        var sweep = ServiceCommandHandler.DescribeSweep(Local(intervalDays: 3), ledger);

        Assert.IsNotNull(sweep);
        Assert.AreEqual(3, sweep.IntervalDays);
        Assert.AreEqual(5_000UL, sweep.CircuitClosedAt);
        Assert.AreEqual(9_000UL, sweep.LastReadAt);
        Assert.AreEqual(4, sweep.ReadThisCircuit);
        Assert.AreEqual(2, sweep.Stalls);
        Assert.AreEqual("blobs/data/ab/abcdef", sweep.StalledOn);
    }

    private static DestinationConfiguration Local(int? intervalDays) => new()
    {
        Id = new string('1', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = "/media/vault",
        DeepVerifyIntervalDays = intervalDays,
    };

    private static DestinationConfiguration Peer(int? intervalDays) => new()
    {
        Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer, Endpoint = "192.0.2.7:7443",
        DeepVerifyIntervalDays = intervalDays,
    };

    private static DestinationConfiguration Declared(DestinationKind kind, int? intervalDays) => new()
    {
        Id = new string('c', 32), Name = "cloud", Kind = kind, DeepVerifyIntervalDays = intervalDays,
    };
}
