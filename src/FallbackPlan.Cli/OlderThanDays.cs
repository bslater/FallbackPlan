using System.Globalization;

namespace FallbackPlan.Cli;

/// <summary>
/// The age <c>--older-than-days</c> judges by (ADR-0164, ADR-0165): what is
/// older than the days a person names ended, or began, before the cutoff.
/// </summary>
internal static class OlderThanDays
{
    /// <summary>
    /// The Unix milliseconds before which what is judged is more than
    /// <paramref name="days"/> days old, never before the epoch however many
    /// days are named.
    /// </summary>
    public static ulong Cutoff(int days) =>
        (ulong)Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (days * 86_400_000L));

    /// <summary>The days, as a person reads them.</summary>
    public static string Days(int days) =>
        string.Create(CultureInfo.InvariantCulture, $"{days} {(days == 1 ? "day" : "days")}");
}
