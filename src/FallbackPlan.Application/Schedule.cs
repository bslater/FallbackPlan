using Bodu;
using System.Globalization;
using Bodu.Globalization.Recurrence;

namespace FallbackPlan.Application;

/// <summary>
/// A backup set's schedule (ADR-0027 §1): <c>every &lt;n&gt;&lt;unit&gt;</c>
/// (m/h/d) anchored to the last completed run, or <c>daily at HH:mm</c>
/// local time. Every derivation is a pure function of its arguments —
/// no correctness property depends on wall time (NFR-TIME-001) — and
/// missed runs coalesce structurally: "is a run due" is a boolean, so an
/// Agent off through five scheduled times owes exactly one catch-up run.
/// </summary>
/// <remarks>
/// The grammar is ours; the occurrence arithmetic is
/// <c>Bodu.Globalization.Recurrence</c>'s. <c>every &lt;n&gt;&lt;unit&gt;</c>
/// becomes an <see cref="AnchoredInterval" /> — the series
/// <c>anchor + k·interval</c> that neither cron nor RRULE can express,
/// because the anchor is the last completed run rather than a calendar
/// position — and <c>daily at HH:mm</c> becomes a
/// <see cref="CronExpression" />. That library forbids itself the wall
/// clock and the machine time zone (its purity test scans the compiled
/// assembly for them), which is the property this type's contract rests
/// on; see docs/bodu-recurrence-requirements.md.
/// </remarks>
public sealed class Schedule
{
    private readonly AnchoredInterval? _interval;
    private readonly CronExpression? _daily;

    private Schedule(AnchoredInterval? interval, CronExpression? daily)
    {
        _interval = interval;
        _daily = daily;
    }

    /// <summary>The original text, for display and round-tripping.</summary>
    public required string Text { get; init; }

    /// <summary>Parses a schedule string; strict, with the defect named.</summary>
    public static bool TryParse(string text, out Schedule? schedule, out string? defect)
    {
        ThrowHelper.ThrowIfNull(text);
        schedule = null;
        defect = null;

        var trimmed = text.Trim();

        if (trimmed.StartsWith("every ", StringComparison.Ordinal))
        {
            var rest = trimmed["every ".Length..].Trim();
            if (rest.Length < 2 || !uint.TryParse(rest[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count == 0)
            {
                defect = $"'{text}': expected 'every <n><unit>' with a positive number.";
                return false;
            }

            TimeSpan unit;
            switch (rest[^1])
            {
                case 'm':
                    unit = TimeSpan.FromMinutes(1);
                    break;
                case 'h':
                    unit = TimeSpan.FromHours(1);
                    break;
                case 'd':
                    unit = TimeSpan.FromDays(1);
                    break;
                default:
                    defect = $"'{text}': unit must be m, h, or d.";
                    return false;
            }

            // The grammar's own bounds have already been checked, so the
            // interval is positive and whole-second by construction; a
            // refusal here would be a defect in this method, not in input.
            if (!AnchoredInterval.TryParse(ToIso8601Duration(unit * count), out var interval, out var intervalDefect))
            {
                defect = $"'{text}': {intervalDefect}";
                return false;
            }

            schedule = new Schedule(interval, daily: null) { Text = trimmed };
            return true;
        }

        if (trimmed.StartsWith("daily at ", StringComparison.Ordinal))
        {
            if (!TimeOnly.TryParseExact(
                    trimmed["daily at ".Length..].Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at))
            {
                defect = $"'{text}': expected 'daily at HH:mm' (24-hour).";
                return false;
            }

            var cron = string.Create(
                CultureInfo.InvariantCulture, $"{at.Minute} {at.Hour} * * *");
            if (!CronExpression.TryParse(cron, out var daily, out var cronDefect))
            {
                defect = $"'{text}': {cronDefect}";
                return false;
            }

            schedule = new Schedule(interval: null, daily) { Text = trimmed };
            return true;
        }

        defect = $"'{text}': a schedule is 'every <n><unit>' or 'daily at HH:mm'.";
        return false;
    }

    /// <summary>
    /// Whether a run is due at <paramref name="now"/>, given the last
    /// COMPLETED run. Null <paramref name="lastCompleted"/> means the set
    /// has never run — always due. Times are compared in the offset the
    /// caller supplies; a wrong clock mis-times a backup, never corrupts
    /// one.
    /// </summary>
    public bool IsDue(DateTimeOffset? lastCompleted, DateTimeOffset now)
    {
        if (lastCompleted is null)
        {
            return true;
        }

        // Due when a scheduled occurrence has passed that the last completed
        // run predates. Because the answer is that single comparison, missed
        // occurrences coalesce: five slept-through times owe exactly one run.
        if (_interval is { } interval)
        {
            return interval.GetPreviousOccurrence(lastCompleted.Value, now, inclusive: true) is not null;
        }

        var occurrence = _daily!.GetPreviousOccurrence(now, inclusive: true);
        return occurrence is not null && lastCompleted.Value < occurrence.Value;
    }

    /// <summary>The next scheduled run after <paramref name="now"/> — the status display's "next run".</summary>
    public DateTimeOffset NextRun(DateTimeOffset? lastCompleted, DateTimeOffset now)
    {
        if (IsDue(lastCompleted, now))
        {
            return now;
        }

        // Not due, so lastCompleted is present: a never-run set is always due.
        return _interval is { } interval
            ? interval.GetNextOccurrence(lastCompleted!.Value, now) ?? now
            : _daily!.GetNextOccurrence(now) ?? now;
    }

    /// <summary>
    /// Renders a whole-second interval as the RFC 5545 duration the
    /// recurrence engine parses — the grammar's units map exactly.
    /// </summary>
    private static string ToIso8601Duration(TimeSpan interval) =>
        string.Create(CultureInfo.InvariantCulture, $"PT{(long)interval.TotalMinutes}M");
}
