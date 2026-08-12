using Bodu;
using FallbackPlan.Application;

namespace FallbackPlan.Retention;

/// <summary>One snapshot as retention sees it.</summary>
/// <param name="SnapshotId">The snapshot's hex identity.</param>
/// <param name="CapturedAtUnixMilliseconds">When it was captured — what the policy rules evaluate.</param>
/// <param name="PublicationSequence">
/// The writer's journal sequence it published under — the standalone
/// record's counter, the one per-publication monotonic a single-writer
/// staging archive has. The replication gate compares it to each
/// destination's synced sequence, never a clock (FR-GC-009).
/// </param>
public sealed record SnapshotFact(
    string SnapshotId, ulong CapturedAtUnixMilliseconds, ulong PublicationSequence = 0);

/// <summary>A kept snapshot and every rule that keeps it — the dry-run report's vocabulary (FR-GC-005).</summary>
/// <param name="Snapshot">The snapshot.</param>
/// <param name="Reasons">The rules that selected it, e.g. <c>daily 2026-08-11</c>, <c>min-generations</c>.</param>
public sealed record SnapshotKeep(SnapshotFact Snapshot, IReadOnlyList<string> Reasons);

/// <summary>What a policy selects: the protected snapshots with reasons, and the rest.</summary>
/// <param name="Keep">Kept snapshots, newest first.</param>
/// <param name="Expire">Snapshots no rule protects, newest first.</param>
public sealed record RetentionSelection(IReadOnlyList<SnapshotKeep> Keep, IReadOnlyList<SnapshotFact> Expire);

/// <summary>
/// Retention's whole authority: evaluate policy, mark what remains protected,
/// delete nothing (architecture 07 §1, FR-GC-001). Pure — snapshots and a
/// clock in, a selection out — so the same derivation serves the dry-run
/// report, the staging collector, and later each destination's keep-set
/// (FR-GC-010).
/// </summary>
public static class RetentionPlanner
{
    /// <summary>Selects the snapshots the policy protects.</summary>
    /// <param name="snapshots">Every snapshot of the set, any order.</param>
    /// <param name="policy">The set's policy, or a destination override (FR-GC-010).</param>
    /// <param name="now">The clock, passed in so the derivation stays pure.</param>
    /// <returns>The selection. With no rule configured, everything is kept — the default is never the destructive reading.</returns>
    public static RetentionSelection Select(
        IReadOnlyList<SnapshotFact> snapshots, RetentionConfiguration policy, DateTimeOffset now)
    {
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNull(policy);

        // Newest first; ties broken by identity so input order never decides
        // what survives.
        var ordered = snapshots
            .OrderByDescending(snapshot => snapshot.CapturedAtUnixMilliseconds)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToList();

        var reasons = ordered.ToDictionary(snapshot => snapshot, _ => new List<string>());

        if (policy.KeepDaily is null && policy.KeepWeekly is null
            && policy.KeepMonthly is null && policy.MinGenerations is null)
        {
            // An absent policy keeps everything: nothing expires until a
            // human writes a rule that says so.
            foreach (var list in reasons.Values)
            {
                list.Add("no rule configured");
            }
        }

        if (policy.KeepDaily is { } days)
        {
            KeepNewestPerBucket(
                ordered, reasons, now.AddDays(-days),
                at => $"daily {at:yyyy-MM-dd}");
        }

        if (policy.KeepWeekly is { } weeks)
        {
            KeepNewestPerBucket(
                ordered, reasons, now.AddDays(-7 * weeks),
                at => $"weekly {System.Globalization.ISOWeek.GetYear(at.UtcDateTime)}-W{System.Globalization.ISOWeek.GetWeekOfYear(at.UtcDateTime):00}");
        }

        if (policy.KeepMonthly is { } months)
        {
            KeepNewestPerBucket(
                ordered, reasons, now.AddMonths(-months),
                at => $"monthly {at:yyyy-MM}");
        }

        if (policy.MinGenerations is { } floor)
        {
            // The floor the other rules cannot override (architecture 07 §2):
            // the newest N stay whatever their age, so a stalled schedule or a
            // long offline period cannot leave a set with nothing.
            foreach (var snapshot in ordered.Take(floor))
            {
                reasons[snapshot].Add("min-generations");
            }
        }

        var keep = new List<SnapshotKeep>();
        var expire = new List<SnapshotFact>();
        foreach (var snapshot in ordered)
        {
            if (reasons[snapshot].Count > 0)
            {
                keep.Add(new SnapshotKeep(snapshot, reasons[snapshot]));
            }
            else
            {
                expire.Add(snapshot);
            }
        }

        return new RetentionSelection(keep, expire);
    }

    /// <summary>
    /// Keeps the newest snapshot of each bucket (day, week, month) whose
    /// capture time falls inside the window.
    /// </summary>
    private static void KeepNewestPerBucket(
        List<SnapshotFact> newestFirst,
        Dictionary<SnapshotFact, List<string>> reasons,
        DateTimeOffset windowStart,
        Func<DateTimeOffset, string> bucketOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in newestFirst)
        {
            var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAtUnixMilliseconds);
            if (capturedAt < windowStart)
            {
                continue;
            }

            var bucket = bucketOf(capturedAt);
            if (seen.Add(bucket))
            {
                reasons[snapshot].Add(bucket);
            }
        }
    }
}
