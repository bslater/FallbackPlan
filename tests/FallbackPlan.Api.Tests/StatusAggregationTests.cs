using FallbackPlan.Api;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// NFR-OPS-006 and ADR-0028 §8: a fleet view is where one green tick per
/// machine is most tempting and most wrong.
/// </summary>
public sealed class StatusAggregationTests
{
    private const ulong Now = 1_700_000_000_000;

    [Fact]
    public void An_unreachable_service_is_stale_with_an_age_never_healthy_and_never_failed()
    {
        var observation = new MachineObservation(
            "laptop",
            Status: Status(ProtectionState.Protected),
            LastContactUnixMilliseconds: Now - 90_000,
            Reachable: false);

        var summary = StatusRollup.Summarise(observation, Now);

        Assert.True(summary.IsStale);
        Assert.Equal(90_000UL, summary.StaleForMilliseconds);

        // Not healthy: the last thing heard was good news, and the console does
        // not know whether it is still true. Not failed either — neither is known.
        Assert.Null(summary.State);
    }

    [Fact]
    public void A_machine_never_heard_from_is_stale_rather_than_never_backed_up()
    {
        var summary = StatusRollup.Summarise(
            new MachineObservation("new-machine", Status: null, LastContactUnixMilliseconds: 0, Reachable: false),
            Now);

        Assert.True(summary.IsStale);
        Assert.Null(summary.State);
        Assert.Empty(summary.Detail);
    }

    [Fact]
    public void A_summary_is_derived_from_detail_that_stays_reachable()
    {
        var observation = new MachineObservation(
            "desktop",
            Status(ProtectionState.Verified, ProtectionState.Degraded),
            Now,
            Reachable: true);

        var summary = StatusRollup.Summarise(observation, Now);

        Assert.False(summary.IsStale);
        Assert.Equal(ProtectionState.Degraded, summary.State);

        // The detail travels with the summary. A roll-up that discarded it
        // would be the only copy of the answer, and the never-merge rules only
        // hold while the components remain visible.
        Assert.Equal(2, summary.Detail.Count);
        Assert.Contains(summary.Detail, set => set.Status.State == ProtectionState.Verified);
    }

    [Fact]
    public void Degraded_and_unrecoverable_never_merge()
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

        Assert.Equal(ProtectionState.Unrecoverable, summary.State);
        Assert.Contains(summary.Detail, set => set.Status.State == ProtectionState.Degraded);
    }

    [Fact]
    public void Never_backed_up_outranks_a_protected_sibling_in_the_headline()
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

        Assert.Equal(ProtectionState.NeverBackedUp, summary.State);
    }

    [Fact]
    public void A_roll_up_never_invents_a_state_outside_the_vocabulary()
    {
        var every = Enum.GetValues<ProtectionState>();
        var summary = StatusRollup.Summarise(
            new MachineObservation("everything", Status(every), Now, Reachable: true),
            Now);

        Assert.Contains(summary.State!.Value, every);
    }

    [Fact]
    public void A_fleet_summarises_machine_by_machine()
    {
        var fleet = StatusRollup.Summarise(
            [
                new MachineObservation("a", Status(ProtectionState.Protected), Now, Reachable: true),
                new MachineObservation("b", Status: null, LastContactUnixMilliseconds: Now - 5_000, Reachable: false),
            ],
            Now);

        Assert.Equal(2, fleet.Count);
        Assert.False(fleet[0].IsStale);
        Assert.True(fleet[1].IsStale);
    }

    private static StatusResult Status(params ProtectionState[] states) =>
        new(
            "machine",
            [.. states.Select((state, index) =>
                new BackupSetStatusDescriptor(
                    $"set-{index}",
                    new BackupSetStatus(state, null, []),
                    NextRun: null))],
            Now);
}
