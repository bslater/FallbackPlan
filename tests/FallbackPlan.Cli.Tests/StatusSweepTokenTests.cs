using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The deep sweep's token on a destination's row of <c>status</c>
/// (FR-VER-003, contract 1.46): the day every stored object there was last
/// read back and matched its seal, or why there is no such day.
/// </summary>
/// <remarks>
/// Kept apart as the drill's states are. "never" is printed rather than
/// omitted, because nobody having read a replica back is something to report.
/// "manual" is not "never": a peer whose operator stated no cadence is read
/// only when a person asks, and a row that called it never swept would read as
/// a sweep overdue. And a stall says so whatever closed before it, because a
/// circuit stuck at a blob is the one thing on the row that needs a person.
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

    private static DestinationStatusDescriptor Row(DeepSweepDescriptor? sweep) =>
        new("vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null, "other-drive", "proven", DeepSweep: sweep);
}
