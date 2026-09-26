using Bodu;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Api;

/// <summary>How a console has heard from one machine.</summary>
/// <param name="MachineName">The machine.</param>
/// <param name="Status">Its last reported status, when any was ever received.</param>
/// <param name="LastContactUnixMilliseconds">When it last answered.</param>
/// <param name="Reachable">Whether it answered the most recent attempt.</param>
public sealed record MachineObservation(
    string MachineName,
    StatusResult? Status,
    ulong LastContactUnixMilliseconds,
    bool Reachable);

/// <summary>One machine's place in a fleet view.</summary>
/// <param name="MachineName">The machine.</param>
/// <param name="State">Its worst per-set state, or null when the machine is stale.</param>
/// <param name="IsStale">Whether the console has lost contact.</param>
/// <param name="StaleForMilliseconds">How long contact has been lost, when it has.</param>
/// <param name="Detail">The per-set detail the summary was derived from — always reachable.</param>
public sealed record MachineSummary(
    string MachineName,
    ProtectionState? State,
    bool IsStale,
    ulong StaleForMilliseconds,
    IReadOnlyList<BackupSetStatusDescriptor> Detail);

/// <summary>
/// Aggregation for a console watching several machines (ADR-0028 §8, NFR-OPS-006).
/// </summary>
/// <remarks>
/// <para>
/// A fleet view is exactly where the temptation to show one green tick per
/// machine becomes strongest, and it is where the never-merge rules
/// (NFR-OPS-002) are most easily lost. So aggregation here is <b>derived and
/// always decomposable</b>: a machine's summary is computed from its per-set
/// detail, the detail travels with the summary, and a roll-up never invents a
/// state the architecture 10 §1.1 vocabulary does not have.
/// </para>
/// <para>
/// A machine the console cannot reach is <b>stale, with the age of last
/// contact</b> — never healthy, never failed, because neither is known. "Unknown"
/// displayed honestly is worth more than a green tick that means "I have not
/// heard bad news".
/// </para>
/// </remarks>
public static class StatusRollup
{
    /// <summary>Summarises one machine without discarding its detail.</summary>
    /// <param name="observation">What the console has heard.</param>
    /// <param name="nowUnixMilliseconds">The console's clock, passed in so this stays pure.</param>
    /// <returns>The summary.</returns>
    public static MachineSummary Summarise(MachineObservation observation, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNull(observation);

        if (!observation.Reachable || observation.Status is null)
        {
            var staleFor = nowUnixMilliseconds > observation.LastContactUnixMilliseconds
                ? nowUnixMilliseconds - observation.LastContactUnixMilliseconds
                : 0;

            return new MachineSummary(
                observation.MachineName,
                State: null,
                IsStale: true,
                StaleForMilliseconds: staleFor,
                Detail: observation.Status?.Sets ?? []);
        }

        return new MachineSummary(
            observation.MachineName,
            Worst(observation.Status.Sets),
            IsStale: false,
            StaleForMilliseconds: 0,
            Detail: observation.Status.Sets);
    }

    /// <summary>Summarises a fleet, machine by machine.</summary>
    /// <param name="observations">What the console has heard from each machine.</param>
    /// <param name="nowUnixMilliseconds">The console's clock.</param>
    /// <returns>One summary per machine, in the order given.</returns>
    public static IReadOnlyList<MachineSummary> Summarise(
        IEnumerable<MachineObservation> observations, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNull(observations);
        return [.. observations.Select(observation => Summarise(observation, nowUnixMilliseconds))];
    }

    /// <summary>
    /// The state a machine's headline shows: the worst of its sets, chosen by
    /// how much attention it demands rather than by enum order — because
    /// <c>NeverBackedUp</c> is numerically first and is not the mildest thing
    /// that can be true of a machine.
    /// </summary>
    /// <param name="sets">The per-set detail.</param>
    /// <returns>The worst state present, or null when there are no sets.</returns>
    public static ProtectionState? Worst(IReadOnlyList<BackupSetStatusDescriptor> sets)
    {
        ThrowHelper.ThrowIfNull(sets);

        ProtectionState? worst = null;
        var worstRank = int.MinValue;

        foreach (var set in sets)
        {
            var rank = Severity(set.Status.State);
            if (rank > worstRank)
            {
                worstRank = rank;
                worst = set.Status.State;
            }
        }

        return worst;
    }

    private static int Severity(ProtectionState state) => state switch
    {
        ProtectionState.Unrecoverable => 100,
        ProtectionState.NeverBackedUp => 90,
        ProtectionState.Degraded => 80,
        ProtectionState.Captured => 40,
        ProtectionState.Protected => 30,
        ProtectionState.Verified => 10,
        _ => 50,
    };
}
