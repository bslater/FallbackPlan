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
/// <param name="CaptureStatus">
/// The manifest's <c>capture_status</c>: 1 complete, 2 partial. A partial
/// capture committed a snapshot that does not hold everything asked for, and
/// retention has to know, because a rule satisfied by one is not the guarantee
/// the operator configured. Defaulted to complete so a caller that cannot say
/// gets the reading that keeps more rather than less.
/// </param>
public sealed record SnapshotFact(
    string SnapshotId,
    ulong CapturedAtUnixMilliseconds,
    ulong PublicationSequence = 0,
    byte CaptureStatus = 1)
{
    /// <summary>
    /// Whether the capture holds everything it was asked for. Status 1 alone
    /// qualifies: the format also assigns 2 (partial) and 3 (aborted), the
    /// codec admits all three on read, and a value this build has never heard
    /// of must fail closed — an unknown status filling the min-generations
    /// floor is the same data loss the partial rule exists to prevent.
    /// </summary>
    public bool IsComplete => CaptureStatus == 1;
}

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
            //
            // The N are counted in COMPLETE captures. A floor filled by
            // backups that did not back everything up is the appearance of the
            // guarantee rather than the guarantee, and it is how a set ends up
            // holding one snapshot with a hole in it and every complete one
            // expired. Reaching past a partial keeps it too — the window is
            // everything down to the oldest snapshot the floor needs, so this
            // rule only ever keeps more.
            var boundary = ordered.Where(snapshot => snapshot.IsComplete).Take(floor).LastOrDefault();

            // With no complete capture anywhere there is nothing to reach for,
            // and inventing a refusal would strand the set: fall back to the
            // plain newest-N.
            var window = boundary is null ? floor : ordered.IndexOf(boundary) + 1;

            foreach (var snapshot in ordered.Take(window))
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
    /// capture time falls inside the window — and, when that newest one is a
    /// partial capture, the bucket's newest complete capture as well.
    /// </summary>
    /// <remarks>
    /// Without the second half, a day whose last backup hit an unreadable file
    /// is represented in the archive only by a backup with a hole in it, and
    /// the complete one taken four hours earlier is expired for being older.
    /// The fallback is additive: the bucket's representative is unchanged, one
    /// more snapshot survives, and the extra keep carries its own reason so
    /// the dry-run report explains itself.
    /// </remarks>
    private static void KeepNewestPerBucket(
        List<SnapshotFact> newestFirst,
        Dictionary<SnapshotFact, List<string>> reasons,
        DateTimeOffset windowStart,
        Func<DateTimeOffset, string> bucketOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var completeSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in newestFirst)
        {
            var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)snapshot.CapturedAtUnixMilliseconds);
            if (capturedAt < windowStart)
            {
                continue;
            }

            var bucket = bucketOf(capturedAt);
            var representative = seen.Add(bucket);
            if (representative)
            {
                reasons[snapshot].Add(bucket);
            }

            // The fallback fires only when the representative was partial: a
            // complete representative marks the bucket on its own way past,
            // so nothing further in it qualifies.
            if (snapshot.IsComplete && completeSeen.Add(bucket) && !representative)
            {
                reasons[snapshot].Add($"{bucket} (complete)");
            }
        }
    }
}
