using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// What the host was told to log, and where to put it (ADR-0043 §6).
/// </summary>
/// <remarks>
/// The effective level comes from a flag, the environment, <c>config.json</c>,
/// then <see cref="LogLevel.Information"/>. It is also changeable while the
/// service runs: the level a machine needs is only known once it has already
/// misbehaved, and requiring a restart to raise it is requiring somebody to
/// destroy the evidence first.
/// </remarks>
public sealed record LoggingOptions
{
    /// <summary>The level applied to any category with no more specific rule.</summary>
    public LogLevel Default { get; init; } = LogLevel.Information;

    /// <summary>
    /// Per-category overrides, matched by longest declared prefix — so
    /// <c>FallbackPlan.Repository</c> covers <c>FallbackPlan.Repository.Packing</c>
    /// unless that names itself.
    /// </summary>
    public IReadOnlyDictionary<string, LogLevel> Categories { get; init; } =
        new Dictionary<string, LogLevel>(StringComparer.Ordinal);

    /// <summary>Where the rolling log file lives, or <see langword="null"/> for no file sink.</summary>
    public string? Directory { get; init; }

    /// <summary>How large one log file may grow before it rolls.</summary>
    public long MaximumFileBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>How many rolled files to keep, newest first.</summary>
    public int RetainFiles { get; init; } = 5;

    /// <summary>
    /// How many records the in-memory ring holds for clients to read, rounded
    /// up to a power of two by <see cref="LogRing"/> — so the default is
    /// already one, and the configured value matches the effective one.
    /// </summary>
    public int RingCapacity { get; init; } = 2_048;

    /// <summary>Whether to also write to the console, for a foreground run.</summary>
    public bool Console { get; init; }

    /// <summary>
    /// The level in force for a category, by longest matching prefix.
    /// </summary>
    /// <param name="category">The logger category to resolve.</param>
    public LogLevel LevelFor(string category)
    {
        var best = Default;
        var bestLength = -1;

        foreach (var (prefix, level) in Categories)
        {
            if (prefix.Length > bestLength && category.StartsWith(prefix, StringComparison.Ordinal))
            {
                best = level;
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Parses a level by name, case-insensitively, accepting the spellings a
    /// person types.
    /// </summary>
    /// <param name="text">The level name.</param>
    /// <param name="level">The parsed level.</param>
    /// <returns>Whether <paramref name="text"/> named a level.</returns>
    public static bool TryParseLevel(string? text, out LogLevel level)
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

    /// <summary>
    /// The environment variable that names the level, for a service started by
    /// something with nowhere to put a flag.
    /// </summary>
    public const string LevelVariable = "FALLBACKPLAN_LOG_LEVEL";

    /// <summary>
    /// Resolves the level in force from the places it can be named, in
    /// precedence order: the flag, then the environment, then the
    /// configuration, then <see cref="LogLevel.Information"/>.
    /// </summary>
    /// <param name="flag">What <c>--log-level</c> carried, if anything.</param>
    /// <param name="environment">What <see cref="LevelVariable"/> held, if anything.</param>
    /// <param name="configured">What the configuration named, if anything.</param>
    /// <param name="level">The level in force.</param>
    /// <param name="refusal">Why a named level was not accepted, when one was not.</param>
    /// <param name="fallback">
    /// What applies when nothing named a level. <see cref="LogLevel.Information"/>
    /// for a service, whose log is the record of what it did unattended; a
    /// console passes <see cref="LogLevel.Warning"/>, because its output is the
    /// answer to the command and lifecycle chatter in the middle of a report
    /// is noise rather than diagnosis.
    /// </param>
    /// <returns>Whether every source that named a level named one that exists.</returns>
    /// <remarks>
    /// A source that names a level nobody recognises is refused rather than
    /// ignored: silently falling back to <see cref="LogLevel.Information"/>
    /// after a typo is how somebody spends an afternoon wondering why
    /// <c>--log-level dbeug</c> changed nothing.
    /// </remarks>
    public static bool TryResolveLevel(
        string? flag,
        string? environment,
        LogLevel? configured,
        out LogLevel level,
        out string? refusal,
        LogLevel fallback = LogLevel.Information)
    {
        if (flag is { Length: > 0 })
        {
            refusal = TryParseLevel(flag, out level) ? null : Unrecognised("--log-level", flag);
            return refusal is null;
        }

        if (environment is { Length: > 0 })
        {
            refusal = TryParseLevel(environment, out level) ? null : Unrecognised(LevelVariable, environment);
            return refusal is null;
        }

        level = configured ?? fallback;
        refusal = null;
        return true;
    }

    private static string Unrecognised(string source, string text) =>
        $"{source} '{text}' is not a log level — the levels are {string.Join(", ", LevelNames)}.";

    /// <summary>The names <see cref="TryParseLevel"/> accepts, for help text and refusals.</summary>
    public static IReadOnlyList<string> LevelNames { get; } =
        ["trace", "debug", "information", "warning", "error", "critical", "none"];

    /// <summary>Renders a level in the vocabulary <see cref="TryParseLevel"/> accepts.</summary>
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
}
