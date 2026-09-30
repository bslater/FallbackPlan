using System.Globalization;

namespace FallbackPlan.Domain.Status;

/// <summary>
/// How a text listing says a snapshot's observed clock skew (NFR-TIME-002,
/// ADR-0077): one token, rendered by one routine so the CLI and the
/// standalone recovery tool print the same words for the same manifest.
/// </summary>
/// <remarks>
/// <para>
/// It lives here because the recovery tool's dependency closure reaches
/// Domain and never the CLI (architecture 11 §2).
/// </para>
/// <para>
/// Relative to the peer, never "slow" or "fast": two clocks disagreeing do
/// not say which is wrong. Under two seconds is in step, the reading's own
/// uncertainty being half a round trip; from five minutes the direction is
/// shouted, where this machine's stamps stop being fit to compare with
/// another's.
/// </para>
/// </remarks>
public static class ObservedClockSkewText
{
    /// <summary>
    /// The token for one reading, such as <c>clock:in-step</c>,
    /// <c>clock:4m12s-behind</c> or <c>clock:3h-BEHIND</c>.
    /// </summary>
    /// <param name="observedClockSkewMs">
    /// The peer's clock minus the capturing machine's, in milliseconds, or
    /// null where the capture recorded no reading.
    /// </param>
    /// <returns>The token, or null where there is no reading, which a listing shows by printing nothing.</returns>
    public static string? Token(long? observedClockSkewMs)
    {
        if (observedClockSkewMs is not { } skew)
        {
            return null;
        }

        // Unchecked on purpose: the magnitude of long.MinValue is 2^63, which
        // negation wraps back to and the cast then reads correctly.
        var magnitude = unchecked((ulong)(skew < 0 ? -skew : skew));
        if (magnitude < 2_000)
        {
            return "clock:in-step";
        }

        var direction = skew > 0 ? "behind" : "ahead";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"clock:{Span(magnitude / 1_000)}-{(magnitude >= 300_000 ? direction.ToUpperInvariant() : direction)}");

        // The two largest units, as a person would say it: 4m12s, 2h5m, 1d2h.
        static string Span(ulong seconds)
        {
            var (days, hours, minutes, rest) = (seconds / 86_400, seconds / 3_600 % 24, seconds / 60 % 60, seconds % 60);
            return days > 0 ? Pair(days, "d", hours, "h")
                : hours > 0 ? Pair(hours, "h", minutes, "m")
                : minutes > 0 ? Pair(minutes, "m", rest, "s")
                : string.Create(CultureInfo.InvariantCulture, $"{rest}s");
        }

        static string Pair(ulong major, string majorUnit, ulong minor, string minorUnit) =>
            minor > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{major}{majorUnit}{minor}{minorUnit}")
                : string.Create(CultureInfo.InvariantCulture, $"{major}{majorUnit}");
    }
}
