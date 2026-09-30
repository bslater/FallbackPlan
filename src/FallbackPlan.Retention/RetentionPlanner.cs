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
/// <param name="WriterId">
/// The writer whose sequence <paramref name="PublicationSequence"/> belongs
/// to, as lowercase hex. A sequence is one writer's own (specification 08
/// §2): an archive adopted under a new identity holds two, and one writer's
/// counters say nothing about where another's snapshots sit. Null for a
/// caller that cannot say, whose snapshots are read as one writer's.
/// </param>
public sealed record SnapshotFact(
    string SnapshotId,
    ulong CapturedAtUnixMilliseconds,
    ulong PublicationSequence = 0,
    byte CaptureStatus = 1,
    string? WriterId = null)
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
public sealed record RetentionSelection(IReadOnlyList<SnapshotKeep> Keep, IReadOnlyList<SnapshotFact> Expire)
{
    /// <summary>
    /// The snapshots kept because their capture time is implausible
    /// (FR-GC-012), newest first by that time. Every one is also in
    /// <see cref="Keep"/>, and none is in <see cref="Expire"/>.
    /// </summary>
    public IReadOnlyList<ImplausibleCapture> Implausible { get; init; } = [];
}

/// <summary>Which way an implausible capture time is out of step with its writer's publication order.</summary>
public enum ImplausibleCaptureTime
{
    /// <summary>Dated before snapshots its writer published ahead of it: the clock read behind when it was taken.</summary>
    Behind,

    /// <summary>Dated after snapshots its writer published after it: the clock read ahead when it was taken.</summary>
    Ahead,
}

/// <summary>A snapshot whose capture time does not fit its writer's publication order (FR-GC-012).</summary>
/// <param name="Snapshot">The snapshot.</param>
/// <param name="Direction">Which way it is out of step.</param>
public sealed record ImplausibleCapture(SnapshotFact Snapshot, ImplausibleCaptureTime Direction);

/// <summary>
/// Retention's whole authority: evaluate policy, mark what remains protected,
/// delete nothing (architecture 07 §1, FR-GC-001). Pure — snapshots and a
/// clock in, a selection out — so the same derivation serves the dry-run
/// report, the staging collector, and later each destination's keep-set
/// (FR-GC-010).
/// </summary>
public static class RetentionPlanner
{
    /// <summary>The reason a snapshot dated before earlier publications is kept, in the dry-run report's words.</summary>
    public const string ImplausibleBehindReason = "implausible capture time (dated before earlier publications)";

    /// <summary>The reason a snapshot dated after later publications is kept, in the dry-run report's words.</summary>
    public const string ImplausibleAheadReason = "implausible capture time (dated after later publications)";

    /// <summary>Selects the snapshots the policy protects.</summary>
    /// <param name="snapshots">Every snapshot of the set, any order.</param>
    /// <param name="policy">The set's policy, or a destination override (FR-GC-010).</param>
    /// <param name="now">The clock, passed in so the derivation stays pure.</param>
    /// <param name="clockSkewMargin">
    /// How far a capture time may stray from its writer's publication order
    /// before it is implausible (FR-GC-012): the installation's
    /// <c>clock_skew_margin_hours</c>, a day when omitted, as a configuration
    /// that states none means.
    /// </param>
    /// <returns>
    /// The selection. With no rule configured, everything is kept — the
    /// default is never the destructive reading. A snapshot whose capture time
    /// is implausible is kept whatever the rules say, and neither fills a
    /// min-generations place nor represents a bucket, because its time is
    /// exactly what cannot be relied on.
    /// </returns>
    public static RetentionSelection Select(
        IReadOnlyList<SnapshotFact> snapshots,
        RetentionConfiguration policy,
        DateTimeOffset now,
        TimeSpan? clockSkewMargin = null)
    {
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNull(policy);

        var implausible = FindImplausible(snapshots, now, clockSkewMargin ?? DefaultMargin);
        var doubted = implausible.ToDictionary(finding => finding.Snapshot, finding => finding.Direction);

        // Newest first; ties broken by identity so input order never decides
        // what survives.
        var everything = snapshots
            .OrderByDescending(snapshot => snapshot.CapturedAtUnixMilliseconds)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToList();

        var reasons = everything.ToDictionary(snapshot => snapshot, _ => new List<string>());
        foreach (var (snapshot, direction) in doubted)
        {
            reasons[snapshot].Add(direction == ImplausibleCaptureTime.Behind ? ImplausibleBehindReason : ImplausibleAheadReason);
        }

        // The rules below read capture times, so they read only the times that
        // can be relied on. A flagged snapshot is already kept by its flag.
        var ordered = everything.Where(snapshot => !doubted.ContainsKey(snapshot)).ToList();

        if (policy.KeepDaily is null && policy.KeepWeekly is null
            && policy.KeepMonthly is null && policy.MinGenerations is null)
        {
            // An absent policy keeps everything: nothing expires until a
            // human writes a rule that says so.
            foreach (var snapshot in ordered)
            {
                reasons[snapshot].Add("no rule configured");
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
        foreach (var snapshot in everything)
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

        return new RetentionSelection(keep, expire)
        {
            Implausible = [.. everything.Where(doubted.ContainsKey).Select(snapshot => new ImplausibleCapture(snapshot, doubted[snapshot]))],
        };
    }

    /// <summary>
    /// The snapshots whose capture time does not fit the order their writer
    /// published them in (FR-GC-012, architecture 04 §7): what
    /// <see cref="Select"/> keeps on that account, and what the snapshot
    /// listing marks. It is the same answer whatever the policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A writer's publication sequence rises with every snapshot it publishes
    /// and reads no clock (specification 08 §2), so it is the one order a
    /// wrong clock cannot disturb. The yardstick is the longest run of a
    /// writer's snapshots whose capture times never fall as the sequence
    /// rises. A snapshot outside that run is implausible when its time lies
    /// more than <paramref name="clockSkewMargin"/> before the run's nearest
    /// member published ahead of it, or after the nearest one published after
    /// it. A step inside the margin is a clock being set right, not a clock
    /// that was wrong.
    /// </para>
    /// <para>
    /// Where two runs are equally long, the run with the later times is taken
    /// as the truth. The order cannot say which pair of clocks was wrong, but
    /// the choice decides which snapshots the policy then reads. An
    /// earlier-dated run taken as the truth would be judged old and could
    /// expire. A later-dated run taken as the truth is judged young and kept,
    /// and the earlier one is kept by its flag.
    /// </para>
    /// <para>
    /// <paramref name="now"/> decides only which snapshots may anchor the run:
    /// none dated more than the margin ahead of it. A capture from this
    /// machine's future is judged by the run, like any other snapshot, and is
    /// never flagged on the clock alone. Retention already follows this
    /// machine's calendar (NFR-TIME-001), and flagging on it would make what
    /// retention keeps depend on two clocks agreeing.
    /// </para>
    /// <para>
    /// A snapshot with no sequence (0, from a caller that cannot say) is not
    /// judged, and nor is anything across writers. An archive adopted under a
    /// new identity holds two sequences, and neither orders the other.
    /// </para>
    /// </remarks>
    /// <param name="snapshots">Every snapshot of the set, any order.</param>
    /// <param name="now">This machine's clock, which bounds the times that may anchor the order.</param>
    /// <param name="clockSkewMargin">How far a time may stray before it is implausible.</param>
    /// <returns>The implausible snapshots, newest first by their recorded time.</returns>
    public static IReadOnlyList<ImplausibleCapture> FindImplausible(
        IReadOnlyList<SnapshotFact> snapshots, DateTimeOffset now, TimeSpan clockSkewMargin)
    {
        ThrowHelper.ThrowIfNull(snapshots);
        ArgumentOutOfRangeException.ThrowIfLessThan(clockSkewMargin, TimeSpan.Zero, nameof(clockSkewMargin));

        var margin = (ulong)clockSkewMargin.TotalMilliseconds;
        var nowMs = now.ToUnixTimeMilliseconds();
        var anchorLimit = nowMs < 0 ? margin : SaturatingAdd((ulong)nowMs, margin);

        var found = new List<ImplausibleCapture>();
        foreach (var writer in snapshots
                     .Where(snapshot => snapshot.PublicationSequence > 0)
                     .GroupBy(snapshot => snapshot.WriterId, StringComparer.Ordinal))
        {
            var order = writer
                .OrderBy(snapshot => snapshot.PublicationSequence)
                .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
                .ToList();
            var run = LongestRisingRun(order, anchorLimit);

            for (var index = 0; index < order.Count; index++)
            {
                if (run[index])
                {
                    continue;
                }

                var at = order[index].CapturedAtUnixMilliseconds;
                var before = Array.LastIndexOf(run, true, index);
                var after = Array.IndexOf(run, true, index);
                if (before >= 0 && order[before].CapturedAtUnixMilliseconds > at
                    && order[before].CapturedAtUnixMilliseconds - at > margin)
                {
                    found.Add(new ImplausibleCapture(order[index], ImplausibleCaptureTime.Behind));
                }
                else if (after >= 0 && at > order[after].CapturedAtUnixMilliseconds
                         && at - order[after].CapturedAtUnixMilliseconds > margin)
                {
                    found.Add(new ImplausibleCapture(order[index], ImplausibleCaptureTime.Ahead));
                }
            }
        }

        return
        [
            .. found
                .OrderByDescending(finding => finding.Snapshot.CapturedAtUnixMilliseconds)
                .ThenBy(finding => finding.Snapshot.SnapshotId, StringComparer.Ordinal),
        ];
    }

    private static readonly TimeSpan DefaultMargin = TimeSpan.FromHours(ClientConfiguration.DefaultClockSkewMarginHours);

    /// <summary>
    /// Which of one writer's snapshots, in publication order, make up the
    /// longest run whose capture times never fall, drawn from those dated no
    /// later than <paramref name="anchorLimit"/>. Among runs of that length it
    /// is the one whose earliest publication carries the latest time.
    /// </summary>
    /// <remarks>
    /// Scanned from the newest publication back, the run is the longest whose
    /// times never rise. Patience sorting finds it in n log n, keeping for
    /// each length the latest time a run of that length can reach back to.
    /// </remarks>
    private static bool[] LongestRisingRun(List<SnapshotFact> order, ulong anchorLimit)
    {
        var tails = new List<int>();
        var previous = new int[order.Count];
        for (var index = order.Count - 1; index >= 0; index--)
        {
            var at = order[index].CapturedAtUnixMilliseconds;
            if (at > anchorLimit)
            {
                continue;
            }

            // The tails' times never rise with their length, so the first
            // tail this time is later than is where it belongs.
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (order[tails[middle]].CapturedAtUnixMilliseconds < at)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            previous[index] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count)
            {
                tails.Add(index);
            }
            else
            {
                tails[low] = index;
            }
        }

        var run = new bool[order.Count];
        for (var at = tails.Count > 0 ? tails[^1] : -1; at >= 0; at = previous[at])
        {
            run[at] = true;
        }

        return run;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    /// <summary>
    /// A capture time as the calendar the rules bucket by. A stamp past what
    /// <see cref="DateTimeOffset"/> can hold is the calendar's last instant:
    /// from the future, as a stamp ahead of this clock always has been, rather
    /// than an exception, or the 1969 a signed reading makes of the largest.
    /// </summary>
    private static DateTimeOffset CalendarTime(ulong unixMilliseconds) =>
        unixMilliseconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds);

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
            var capturedAt = CalendarTime(snapshot.CapturedAtUnixMilliseconds);
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
