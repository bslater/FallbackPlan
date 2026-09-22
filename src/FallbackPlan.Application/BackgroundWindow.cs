using Bodu;
using System.Globalization;

namespace FallbackPlan.Application;

/// <summary>
/// The hours background activity may run in — the time-window half of
/// NFR-PERF-013, and the first of that requirement's four limits to exist
/// (ADR-0069). The grammar is
/// <c>HH:mm-HH:mm</c> in local wall-clock time, and a window whose start is
/// after its end crosses midnight, because <c>22:00-06:00</c> is the one a
/// person actually writes.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="Schedule"/> and for the same reason: every
/// derivation is a function of its arguments and this type reads no clock of
/// its own, so a test states the time rather than waiting for it.
/// </para>
/// <para>
/// A window is a **wall-clock** range, not a span of instants. It says which
/// positions on this machine's clock face background work may start in, so a
/// daylight-saving transition shortens or lengthens the night by an hour and
/// nothing is done about that — an operator who writes "not while I am
/// working" means the clock on the wall. NFR-TIME-001 is untouched: this is a
/// policy about one machine's clock, and no correctness property depends on
/// it. A wrong clock mis-times a backup; it never corrupts one.
/// </para>
/// </remarks>
public sealed class BackgroundWindow
{
    private readonly TimeOnly _opens;
    private readonly TimeOnly _closes;

    private BackgroundWindow(TimeOnly opens, TimeOnly closes)
    {
        _opens = opens;
        _closes = closes;
    }

    /// <summary>The original text, for display and round-tripping.</summary>
    public required string Text { get; init; }

    /// <summary>Whether this window crosses midnight — <c>22:00-06:00</c> rather than <c>09:00-17:00</c>.</summary>
    public bool CrossesMidnight => _opens > _closes;

    /// <summary>Parses a window; strict, with the defect named.</summary>
    /// <param name="text">The window text, <c>HH:mm-HH:mm</c>.</param>
    /// <param name="window">The parsed window, on success.</param>
    /// <param name="defect">What was wrong with the text, on failure.</param>
    /// <returns>Whether the text is a window.</returns>
    public static bool TryParse(string text, out BackgroundWindow? window, out string? defect)
    {
        ThrowHelper.ThrowIfNull(text);
        window = null;
        defect = null;

        var trimmed = text.Trim();
        var separator = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            defect = $"'{text}': a background window is 'HH:mm-HH:mm' (24-hour, local time).";
            return false;
        }

        if (!TimeOnly.TryParseExact(
                trimmed[..separator].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var opens)
            || !TimeOnly.TryParseExact(
                trimmed[(separator + 1)..].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var closes))
        {
            defect = $"'{text}': both ends must be HH:mm (24-hour, local time).";
            return false;
        }

        if (opens == closes)
        {
            // Refused rather than guessed. The two readings — always open and
            // never open — are equally defensible and one of them silently
            // stops every backup, which is the worst thing a misread setting
            // can do in a backup product. Leaving the setting out is how
            // "always" is said.
            defect = $"'{text}': a window that opens and closes at the same time says nothing; omit it to run at any hour.";
            return false;
        }

        window = new BackgroundWindow(opens, closes) { Text = trimmed };
        return true;
    }

    /// <summary>
    /// Whether background activity may run at <paramref name="now"/>. Half
    /// open at the end: a <c>22:00-06:00</c> window is shut at exactly 06:00.
    /// </summary>
    /// <param name="now">The operator's wall clock — <see cref="DateTimeOffset.Now"/>, not a UTC instant.</param>
    /// <returns>Whether the window is open.</returns>
    public bool IsOpen(DateTimeOffset now)
    {
        var at = TimeOnly.FromDateTime(now.DateTime);
        return CrossesMidnight
            ? at >= _opens || at < _closes
            : at >= _opens && at < _closes;
    }

    /// <summary>When this window next opens, or <paramref name="now"/> when it is open already.</summary>
    /// <param name="now">The operator's wall clock.</param>
    /// <returns>The next opening.</returns>
    public DateTimeOffset NextOpen(DateTimeOffset now) =>
        IsOpen(now) ? now : now + Until(_opens, now);

    /// <summary>When this window next closes, or <paramref name="now"/> when it is shut already.</summary>
    /// <param name="now">The operator's wall clock.</param>
    /// <returns>The next closing.</returns>
    public DateTimeOffset NextClose(DateTimeOffset now) =>
        IsOpen(now) ? now + Until(_closes, now) : now;

    /// <summary>
    /// The wall-clock span forward from <paramref name="now"/> to the next
    /// time the clock reads <paramref name="boundary"/>; a whole day when it
    /// reads it now.
    /// </summary>
    /// <remarks>
    /// Added to an instant, which is what makes the answer a display figure
    /// rather than a decision: across a daylight-saving transition the instant
    /// it names is an hour out. Nothing gates on it — <see cref="IsOpen"/> is
    /// what the scheduler asks — so the drift costs a line of status text and
    /// never a run.
    /// </remarks>
    private static TimeSpan Until(TimeOnly boundary, DateTimeOffset now)
    {
        var span = boundary - TimeOnly.FromDateTime(now.DateTime);
        return span > TimeSpan.Zero ? span : span + TimeSpan.FromDays(1);
    }
}
