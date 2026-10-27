using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The deep sweep's token on a destination's row of <c>status</c>
/// (FR-VER-003, contract 1.46): the day every stored object there was last
/// read back and matched its seal, or why there is no such day. A staging
/// set's line carries its own staging archive's sweep the same way
/// (FR-VER-009, contract 1.63).
/// </summary>
/// <remarks>
/// Kept apart as the drill's states are. "never" is printed rather than
/// omitted, because nobody having read a replica back is something to report.
/// "manual" is not "never": a peer whose operator stated no cadence is read
/// only when a person asks, and a row that called it never swept would read as
/// a sweep overdue. And a stall says so whatever closed before it, because a
/// circuit stuck at a blob is the one thing on the row that needs a person.
/// A circuit that closed past blobs the store holds only in an archival tier
/// did not read every stored object, so it is not "ok" (ADR-0106).
/// </remarks>
[TestClass]
public sealed class StatusSweepTokenTests
{
    [TestMethod]
    public void ACircuitThatClosed_PrintsTheDayItClosed()
    {
        var closed = (ulong)DateTimeOffset.Parse("2026-09-25T10:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual(
            "sweep:ok@2026-09-25",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(IntervalDays: 7, CircuitClosedAt: closed))));
    }

    [TestMethod]
    public void ACircuitThatClosedPastArchivedBlobs_PrintsPartial_NotOk()
    {
        var closed = (ulong)DateTimeOffset.Parse("2026-09-25T10:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual(
            "sweep:partial@2026-09-25",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(IntervalDays: 7, CircuitClosedAt: closed, CircuitArchived: 3))));
    }

    [TestMethod]
    public void ADestinationNeverReadBack_PrintsNever_RatherThanNothing() =>
        Assert.AreEqual(
            "sweep:never",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(IntervalDays: 7, CircuitClosedAt: null))));

    [TestMethod]
    public void AFirstCircuitUnderWay_SaysSo_WithoutClaimingOneClosed() =>
        Assert.AreEqual(
            "sweep:under-way",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(
                IntervalDays: 7, CircuitClosedAt: null, ReadThisCircuit: 4, LastReadAt: 9_000))));

    [TestMethod]
    public void APeerNothingSweepsOnASchedule_PrintsManual_NotNever() =>
        Assert.AreEqual(
            "sweep:manual",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(IntervalDays: null, CircuitClosedAt: null))));

    [TestMethod]
    public void AStalledCircuit_SaysStalled_WhateverClosedBefore() =>
        Assert.AreEqual(
            "sweep:STALLED",
            CliApplication.DescribeSweep(Row(new DeepSweepDescriptor(
                IntervalDays: 7, CircuitClosedAt: 5_000, ReadThisCircuit: 1, LastReadAt: 9_000,
                Stalls: 2, StalledOn: "blobs/data/ab/abcdef"))));

    [TestMethod]
    public void ARowWithNoSweep_PrintsNoToken()
    {
        // A kind nothing reads back in full, or a service older than 1.46:
        // there is no sweep to describe, and "never" would claim one.
        Assert.AreEqual(string.Empty, CliApplication.DescribeSweep(Row(sweep: null)));
    }

    [TestMethod]
    public void AStagingArchiveWhoseCircuitClosed_PrintsTheDayItClosed_OnTheSetsLine()
    {
        var closed = (ulong)DateTimeOffset.Parse("2026-09-25T10:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual(
            "staging-sweep:ok@2026-09-25",
            CliApplication.DescribeStagingSweep(Set(new DeepSweepDescriptor(IntervalDays: 7, CircuitClosedAt: closed))));
    }

    [TestMethod]
    public void AStagingSweepThatStalled_SaysStalled() =>
        Assert.AreEqual(
            "staging-sweep:STALLED",
            CliApplication.DescribeStagingSweep(Set(new DeepSweepDescriptor(
                IntervalDays: 7, CircuitClosedAt: 5_000, Stalls: 1, StalledOn: "blobs/data/ab/abcdef"))));

    [TestMethod]
    public void ASetWithNoStagingArchive_PrintsNoStagingToken() =>
        // A direct-ship set, or a service older than 1.63.
        Assert.AreEqual(string.Empty, CliApplication.DescribeStagingSweep(Set(sweep: null)));

    private static BackupSetStatusDescriptor Set(DeepSweepDescriptor? sweep) =>
        new("docs", new FallbackPlan.Domain.Status.BackupSetStatus(FallbackPlan.Domain.Status.ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], StagingSweep: sweep);

    private static DestinationStatusDescriptor Row(DeepSweepDescriptor? sweep) =>
        new("vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null, "other-drive", "proven", DeepSweep: sweep);
}
