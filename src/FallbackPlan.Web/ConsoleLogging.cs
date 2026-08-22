using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Web;

/// <summary>
/// The web console's sink: one writer, one level, no packages (ADR-0043 §1).
/// </summary>
/// <remarks>
/// <para>
/// The service and the CLI share <c>FallbackPlan.Diagnostics</c>, which owns a
/// ring buffer, a rolling file and a redacting renderer. The console
/// references none of it, and must not: <c>DependencyRuleTests</c> pins this
/// assembly's project references to the client contract, because a front end
/// that could reach the engine would be a second writer (11 §2, ADR-0036). The
/// duplication with the recovery tool's copy is the price of that rule and is
/// meant to be visible.
/// </para>
/// <para>
/// Nothing is redacted, because nothing crosses a boundary: this writes to the
/// terminal the operator started the console from, on the machine already
/// holding the files. What the browser is shown comes from the service.
/// </para>
/// </remarks>
internal static class ConsoleLogging
{
    /// <summary>The environment variable that names the level when no flag does.</summary>
    internal const string LevelVariable = "FALLBACKPLAN_LOG_LEVEL";

    /// <summary>The levels a person may name.</summary>
    internal static readonly string[] LevelNames =
        ["trace", "debug", "information", "warning", "error", "critical", "none"];

    /// <summary>
    /// Resolves the level in force: the flag, then the environment, then
    /// <see cref="LogLevel.Warning"/> — the console's own start-up lines are
    /// its output, so what is logged beneath a warning is only what somebody
    /// asked to see.
    /// </summary>
    /// <param name="flag">What <c>--log-level</c> carried, if anything.</param>
    /// <param name="level">The level in force.</param>
    /// <param name="refusal">Why a named level was not accepted, when one was not.</param>
    /// <returns>Whether every source that named a level named one that exists.</returns>
    internal static bool TryResolveLevel(string? flag, out LogLevel level, out string? refusal)
    {
        var named = flag is { Length: > 0 } ? flag : Environment.GetEnvironmentVariable(LevelVariable);
        var source = flag is { Length: > 0 } ? "--log-level" : LevelVariable;

        if (named is not { Length: > 0 })
        {
            level = LogLevel.Warning;
            refusal = null;
            return true;
        }

        switch (named.Trim().ToLowerInvariant())
        {
            case "trace" or "verbose": level = LogLevel.Trace; break;
            case "debug": level = LogLevel.Debug; break;
            case "info" or "information": level = LogLevel.Information; break;
            case "warn" or "warning": level = LogLevel.Warning; break;
            case "error": level = LogLevel.Error; break;
            case "critical" or "fatal": level = LogLevel.Critical; break;
            case "none" or "off": level = LogLevel.None; break;
            default:
                level = LogLevel.Warning;
                refusal = $"{source} '{named}' is not a log level — the levels are {string.Join(", ", LevelNames)}.";
                return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>A logger writing to <paramref name="writer"/> at or above <paramref name="minimum"/>.</summary>
    /// <param name="writer">Where lines go.</param>
    /// <param name="minimum">The level in force.</param>
    /// <param name="category">The category the records carry.</param>
    internal static ILogger For(TextWriter writer, LogLevel minimum, string category) =>
        minimum == LogLevel.None ? NullLogger.Instance : new ConsoleLogger(writer, minimum, category);

    private sealed class ConsoleLogger(TextWriter writer, LogLevel minimum, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            writer.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{LevelNames[(int)logLevel],-11}  {eventId.Id,-5}  {category}  {formatter(state, exception)}"));

            if (exception is not null)
            {
                writer.WriteLine($"{exception.GetType().FullName}: {exception.Message}");
            }
        }
    }
}
