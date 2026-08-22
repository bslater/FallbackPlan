using Microsoft.Extensions.Logging;

namespace FallbackPlan.Domain.Diagnostics;

/// <summary>
/// The level vocabulary (ADR-0043 §6): the names a person may write, and what
/// each one means.
/// </summary>
/// <remarks>
/// This sits in Domain because three places need to agree on it and only two
/// of them can see each other. A host parses <c>--log-level</c> and
/// <c>FALLBACKPLAN_LOG_LEVEL</c> through <c>FallbackPlan.Diagnostics</c>;
/// <c>ClientConfiguration</c> validates the <c>logging.level</c> field in
/// <c>FallbackPlan.Application</c>, which must not reference Diagnostics — that
/// would carry the concrete logging package into every assembly that reads a
/// configuration file, which is exactly what ADR-0043 §1 forbids. Two copies of
/// a vocabulary drift, and the drift shows up as a level a file may declare and
/// a flag may not, or the reverse.
/// </remarks>
public static class LogLevels
{
    /// <summary>The canonical names, one per level, for help text and refusals.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["trace", "debug", "information", "warning", "error", "critical", "none"];

    /// <summary>
    /// Parses a level by name, case-insensitively, accepting the spellings a
    /// person types as well as the canonical ones.
    /// </summary>
    /// <param name="text">The level name.</param>
    /// <param name="level">The parsed level.</param>
    /// <returns>Whether <paramref name="text"/> named a level.</returns>
    public static bool TryParse(string? text, out LogLevel level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "trace" or "verbose": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info" or "information": level = LogLevel.Information; return true;
            case "warn" or "warning": level = LogLevel.Warning; return true;
            case "error": level = LogLevel.Error; return true;
            case "critical" or "fatal": level = LogLevel.Critical; return true;
            case "none" or "off": level = LogLevel.None; return true;
            default: level = LogLevel.Information; return false;
        }
    }

    /// <summary>Renders a level in the vocabulary <see cref="TryParse"/> accepts.</summary>
    /// <param name="level">The level to name.</param>
    public static string NameOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };

    /// <summary>The names joined for a refusal message: "trace, debug, …".</summary>
    public static string NameList() => string.Join(", ", Names);
}
