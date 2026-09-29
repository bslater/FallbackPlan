using Bodu;
using System.Globalization;

namespace FallbackPlan.Application;

/// <summary>
/// A byte rate background activity is held to — the disk and network halves
/// of NFR-PERF-013 (ADR-0074), beside the time window ADR-0069 built. The
/// grammar is a whole number and a binary unit per second: <c>512 KiB/s</c>,
/// <c>2 MiB/s</c>, <c>1 GiB/s</c>, or plain <c>B/s</c>.
/// </summary>
/// <remarks>
/// <para>
/// Strict, with the defect named, for the reason <see cref="BackgroundWindow"/>
/// is: a limit this build misreads is either a limit nobody honours or a
/// backup that crawls, and neither shows until somebody wonders why. So the
/// decimal units people type are refused with the binary one named rather
/// than guessed — "MB/s" read as 10^6 is 5% off the same text read as 2^20 —
/// and so is anything slower than 1 KiB/s, which is a stop rather than a
/// limit: leaving the setting out is how "unlimited" is said.
/// </para>
/// </remarks>
public sealed class ByteRate
{
    /// <summary>The slowest rate that is a limit rather than a stop.</summary>
    public const long MinimumBytesPerSecond = 1024;

    private ByteRate(long bytesPerSecond) => BytesPerSecond = bytesPerSecond;

    /// <summary>The configured text, trimmed, for display and round-tripping.</summary>
    public required string Text { get; init; }

    /// <summary>The rate in bytes a second.</summary>
    public long BytesPerSecond { get; }

    /// <summary>Parses a rate; strict, with the defect named.</summary>
    /// <param name="text">The rate text, e.g. <c>2 MiB/s</c>.</param>
    /// <param name="rate">The parsed rate, on success.</param>
    /// <param name="defect">What was wrong with the text, on failure.</param>
    /// <returns>Whether the text is a rate.</returns>
    public static bool TryParse(string text, out ByteRate? rate, out string? defect)
    {
        ThrowHelper.ThrowIfNull(text);
        rate = null;
        defect = null;

        var trimmed = text.Trim();
        if (!trimmed.EndsWith("/s", StringComparison.Ordinal))
        {
            defect = Grammar(text);
            return false;
        }

        var body = trimmed[..^2];
        var digits = 0;
        while (digits < body.Length && char.IsAsciiDigit(body[digits]))
        {
            digits++;
        }

        if (digits == 0)
        {
            defect = Grammar(text);
            return false;
        }

        var unit = body[digits..].Trim();
        var multiplier = unit switch
        {
            "B" => 1L,
            "KiB" => 1L << 10,
            "MiB" => 1L << 20,
            "GiB" => 1L << 30,
            _ => 0L,
        };
        if (multiplier == 0)
        {
            defect = unit is "kB" or "KB" or "MB" or "GB"
                ? $"'{text}': write a binary unit — KiB/s, MiB/s or GiB/s. A decimal one is refused rather than guessed, "
                    + "because the two readings of the same text differ by up to seven percent."
                : Grammar(text);
            return false;
        }

        if (!long.TryParse(body[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count > long.MaxValue / multiplier)
        {
            defect = $"'{text}': too large a rate to represent — leave the setting out for no limit.";
            return false;
        }

        var bytesPerSecond = count * multiplier;
        if (bytesPerSecond < MinimumBytesPerSecond)
        {
            defect = $"'{text}': a background limit is at least 1 KiB/s. Anything slower is a stop rather than a limit; "
                + "leave the setting out for no limit.";
            return false;
        }

        rate = new ByteRate(bytesPerSecond) { Text = trimmed };
        return true;
    }

    private static string Grammar(string text) =>
        $"'{text}': a background limit is a whole number and a unit per second — B/s, KiB/s, MiB/s or GiB/s, "
        + "as in '2 MiB/s'.";
}
