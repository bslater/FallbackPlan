using Bodu;
using FallbackPlan.Application;

namespace FallbackPlan.Retention;

/// <summary>A snapshot the gate is holding, and why.</summary>
/// <param name="Snapshot">The policy-expired snapshot staging must keep for now.</param>
/// <param name="AwaitingDestinations">The declared destinations that have not received it.</param>
/// <param name="DeferralExceeded">
/// Whether a laggard has been behind longer than the configured bound — the
/// point at which holding quietly becomes hiding a problem, and the gap is
/// raised as a warning requiring action (FR-GC-009).
/// </param>
public sealed record HeldSnapshot(
    SnapshotFact Snapshot, IReadOnlyList<string> AwaitingDestinations, bool DeferralExceeded);

/// <summary>The gate's verdict over a policy's expire-set.</summary>
/// <param name="Expirable">Snapshots every configured destination holds — safe to expire from staging.</param>
/// <param name="Held">Snapshots some destination still lacks — kept, each with its laggards named.</param>
public sealed record GateResult(IReadOnlyList<SnapshotFact> Expirable, IReadOnlyList<HeldSnapshot> Held);

/// <summary>
/// Retention must not outrun replication (FR-GC-009, ADR-0011 Amendment 2):
/// a snapshot leaves staging only when every configured destination of its
/// set holds it. The comparison is generation against synced generation —
/// a destination whose last successful sync began at or after a snapshot's
/// publication holds it by construction, and no clock is consulted
/// (ADR-0009's posture). Holding extra snapshots costs disk; expiring them
/// costs history that exists nowhere else. The cheaper failure is the
/// default, and the bound on it is a warning, never a silent expiry.
/// </summary>
public static class ReplicationGate
{
    /// <summary>The deferral bound when the policy does not set one.</summary>
    public const int DefaultDeferralDays = 30;

    /// <summary>Applies the gate to a policy's expire-set.</summary>
    /// <param name="expire">What the policy no longer protects (<see cref="RetentionPlanner"/>).</param>
    /// <param name="destinations">The set's declared destination names — all of them, whatever their kind.</param>
    /// <param name="recordFor">The sync ledger row for a destination, or null when never attempted.</param>
    /// <param name="deferralDays">The configured bound, or null for the default.</param>
    /// <param name="nowUnixMilliseconds">The clock — used only to measure how long a laggard has lagged.</param>
    /// <returns>The verdict.</returns>
    public static GateResult Apply(
        IReadOnlyList<SnapshotFact> expire,
        IReadOnlyList<string> destinations,
        Func<string, DestinationSyncRecord?> recordFor,
        int? deferralDays,
        ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNull(expire);
        ThrowHelper.ThrowIfNull(destinations);
        ThrowHelper.ThrowIfNull(recordFor);

        var boundMs = (ulong)(deferralDays ?? DefaultDeferralDays) * 24UL * 3_600_000UL;
        var records = destinations.ToDictionary(name => name, recordFor, StringComparer.Ordinal);

        var expirable = new List<SnapshotFact>();
        var held = new List<HeldSnapshot>();
        foreach (var snapshot in expire)
        {
            var awaiting = destinations
                .Where(name => (records[name]?.SyncedGeneration ?? 0) < snapshot.PublicationGeneration)
                .ToList();

            if (awaiting.Count == 0)
            {
                expirable.Add(snapshot);
                continue;
            }

            // The laggard's lag, not the snapshot's age: a destination that
            // synced yesterday and misses only this morning's snapshot has
            // lagged a day, however old the archive is. A destination that
            // has never synced has lagged for as long as the snapshot has
            // existed to miss.
            var exceeded = awaiting.Any(name =>
            {
                var since = Math.Max(
                    records[name]?.LastSuccessAt ?? 0, snapshot.CapturedAtUnixMilliseconds);
                return nowUnixMilliseconds > since && nowUnixMilliseconds - since > boundMs;
            });

            held.Add(new HeldSnapshot(snapshot, awaiting, exceeded));
        }

        return new GateResult(expirable, held);
    }
}
