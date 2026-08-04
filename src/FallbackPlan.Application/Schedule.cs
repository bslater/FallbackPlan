using System.Globalization;

namespace FallbackPlan.Application;

/// <summary>
/// A backup set's schedule (ADR-0027 §1): <c>every &lt;n&gt;&lt;unit&gt;</c>
/// (m/h/d) anchored to the last completed run, or <c>daily at HH:mm</c>
/// local time. Every derivation is a pure function of its arguments —
/// no correctness property depends on wall time (NFR-TIME-001) — and
/// missed runs coalesce structurally: "is a run due" is a boolean, so an
/// Agent off through five scheduled times owes exactly one catch-up run.
/// </summary>
public sealed class Schedule
{
    private readonly TimeSpan? _interval;
    private readonly TimeOnly? _dailyAt;

    private Schedule(TimeSpan? interval, TimeOnly? dailyAt)
    {
        _interval = interval;
        _dailyAt = dailyAt;
    }

    /// <summary>The original text, for display and round-tripping.</summary>
    public required string Text { get; init; }

    /// <summary>Parses a schedule string; strict, with the defect named.</summary>
    public static bool TryParse(string text, out Schedule? schedule, out string? defect)
    {
        ArgumentNullException.ThrowIfNull(text);
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

            schedule = new Schedule(unit * count, dailyAt: null) { Text = trimmed };
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

            schedule = new Schedule(interval: null, at) { Text = trimmed };
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

        if (_interval is { } interval)
        {
            return now - lastCompleted.Value >= interval;
        }

        // daily at HH:mm: due when today's occurrence has passed and the
        // last completed run predates it. One run per calendar day. All
        // arithmetic stays in now's own offset — converting through the
        // machine timezone would make the answer depend on where the code
        // runs, not on the arguments (NFR-TIME-001).
        var today = now.Date + _dailyAt!.Value.ToTimeSpan();
        var occurrence = now.DateTime >= today
            ? today
            : today.AddDays(-1);
        return lastCompleted.Value.ToOffset(now.Offset).DateTime < occurrence;
    }

    /// <summary>The next scheduled run after <paramref name="now"/> — the status display's "next run".</summary>
    public DateTimeOffset NextRun(DateTimeOffset? lastCompleted, DateTimeOffset now)
    {
        if (IsDue(lastCompleted, now))
        {
            return now;
        }

        if (_interval is { } interval)
        {
            return lastCompleted!.Value + interval;
        }

        var today = now.Date + _dailyAt!.Value.ToTimeSpan();
        var next = now.DateTime < today ? today : today.AddDays(1);
        return new DateTimeOffset(next, now.Offset);
    }
}
