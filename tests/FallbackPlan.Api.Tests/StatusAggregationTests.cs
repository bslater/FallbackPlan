using FallbackPlan.Api;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// NFR-OPS-006 and ADR-0028 §8: a fleet view is where one green tick per
/// machine is most tempting and most wrong.
/// </summary>
[TestClass]
public sealed class StatusAggregationTests
{
    private const ulong Now = 1_700_000_000_000;

    [TestMethod]
    public void StatusRollUp_ServiceIsUnreachable_ReportsStaleWithAnAgeRatherThanHealthyOrFailed()
    {
        var observation = new MachineObservation(
            "laptop",
            Status: Status(ProtectionState.Protected),
            LastContactUnixMilliseconds: Now - 90_000,
            Reachable: false);

        var summary = StatusRollup.Summarise(observation, Now);

        Assert.IsTrue(summary.IsStale);
        Assert.AreEqual(90_000UL, summary.StaleForMilliseconds);

        // Not healthy: the last thing heard was good news, and the console does
        // not know whether it is still true. Not failed either — neither is known.
        Assert.IsNull(summary.State);
    }

    [TestMethod]
    public void StatusRollUp_MachineNeverHeardFrom_ReportsStaleRatherThanNeverBackedUp()
    {
        var summary = StatusRollup.Summarise(
            new MachineObservation("new-machine", Status: null, LastContactUnixMilliseconds: 0, Reachable: false),
            Now);

        Assert.IsTrue(summary.IsStale);
        Assert.IsNull(summary.State);
        Assert.IsEmpty(summary.Detail);
    }

    [TestMethod]
    public void StatusRollUp_SummaryRequested_KeepsTheDetailItWasDerivedFrom()
    {
        var observation = new MachineObservation(
            "desktop",
            Status(ProtectionState.Verified, ProtectionState.Degraded),
            Now,
            Reachable: true);

        var summary = StatusRollup.Summarise(observation, Now);

        Assert.IsFalse(summary.IsStale);
        Assert.AreEqual(ProtectionState.Degraded, summary.State);

        // The detail travels with the summary. A roll-up that discarded it
        // would be the only copy of the answer, and the never-merge rules only
        // hold while the components remain visible.
        Assert.AreEqual(2, summary.Detail.Count);
        Assert.Contains(set => set.Status.State == ProtectionState.Verified, summary.Detail);
    }

    [TestMethod]
    public void StatusRollUp_DegradedAndUnrecoverableTogether_KeepsThemDistinct()
    {
        // NFR-OPS-002 across machines. Unrecoverable means data is already
        // gone; degraded means act soon. A roll-up that showed one for the
        // other would be the exact failure the vocabulary was closed to prevent.
        var summary = StatusRollup.Summarise(
            new MachineObservation(
                "server",
                Status(ProtectionState.Degraded, ProtectionState.Unrecoverable),
                Now,
                Reachable: true),
            Now);

        Assert.AreEqual(ProtectionState.Unrecoverable, summary.State);
        Assert.Contains(set => set.Status.State == ProtectionState.Degraded, summary.Detail);
    }

    [TestMethod]
    public void StatusRollUp_NeverBackedUpBesideAProtectedSibling_HeadlinesNeverBackedUp()
    {
        // Enum order would put NeverBackedUp first and therefore mildest, which
        // it is not: a set that has never been captured is among the loudest
        // things a machine can say.
        var summary = StatusRollup.Summarise(
            new MachineObservation(
                "laptop",
                Status(ProtectionState.Protected, ProtectionState.NeverBackedUp),
                Now,
                Reachable: true),
            Now);

        Assert.AreEqual(ProtectionState.NeverBackedUp, summary.State);
    }

    [TestMethod]
    public void StatusRollUp_AnyCombinationOfInputs_ReturnsOnlyDefinedStates()
    {
        var every = Enum.GetValues<ProtectionState>();
        var summary = StatusRollup.Summarise(
            new MachineObservation("everything", Status(every), Now, Reachable: true),
            Now);

        Assert.Contains(summary.State!.Value, every);
    }

    [TestMethod]
    public void FleetStatus_SeveralMachines_SummarisesEachSeparately()
    {
        var fleet = StatusRollup.Summarise(
            [
                new MachineObservation("a", Status(ProtectionState.Protected), Now, Reachable: true),
                new MachineObservation("b", Status: null, LastContactUnixMilliseconds: Now - 5_000, Reachable: false),
            ],
            Now);

        Assert.AreEqual(2, fleet.Count);
        Assert.IsFalse(fleet[0].IsStale);
        Assert.IsTrue(fleet[1].IsStale);
    }

    private static StatusResult Status(params ProtectionState[] states) =>
        new(
            "machine",
            [.. states.Select((state, index) =>
                new BackupSetStatusDescriptor(
                    $"set-{index}",
                    new BackupSetStatus(state, null, []),
                    NextRun: null,
                    Destinations: []))],
            Now);
}
