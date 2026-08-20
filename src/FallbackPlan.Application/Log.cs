using Microsoft.Extensions.Logging;

namespace FallbackPlan.Application;

/// <summary>
/// Use-case layer diagnostics (ADR-0043; event ids 3400–3499).
/// </summary>
/// <remarks>
/// Configuration and scheduling decisions are pure functions of their inputs,
/// which makes them easy to test and hard to observe: when a set does not run
/// when somebody expected it to, the arithmetic that decided so left no trace.
/// These record the decision, not the derivation.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3400, Level = LogLevel.Information,
        Message = "Configuration loaded at schema {Schema}: {Sets} sets, {Destinations} destinations")]
    internal static partial void ConfigurationLoaded(
        ILogger logger, int schema, int sets, int destinations);

    [LoggerMessage(
        EventId = 3401, Level = LogLevel.Warning,
        Message = "Configuration refused: {Defect} — {Message}")]
    internal static partial void ConfigurationRefused(ILogger logger, string defect, string message);

    [LoggerMessage(
        EventId = 3402, Level = LogLevel.Information,
        Message = "Configuration migrated from schema {From} to {To}")]
    internal static partial void ConfigurationMigrated(ILogger logger, int from, int to);

    [LoggerMessage(
        EventId = 3410, Level = LogLevel.Debug,
        Message = "Set {SetName} is due: last completed {LastCompleted}, next run {NextRun}")]
    internal static partial void SetDue(
        ILogger logger, string setName, string lastCompleted, string nextRun);

    [LoggerMessage(
        EventId = 3411, Level = LogLevel.Debug,
        Message = "Set {SetName} is not due yet; next run {NextRun}")]
    internal static partial void SetNotDue(ILogger logger, string setName, string nextRun);

    [LoggerMessage(
        EventId = 3412, Level = LogLevel.Information,
        Message = "Coalescing {Missed} missed runs for {SetName} into one catch-up")]
    internal static partial void MissedRunsCoalesced(ILogger logger, int missed, string setName);

    [LoggerMessage(
        EventId = 3420, Level = LogLevel.Information,
        Message = "Notice raised: {Key} — {Message}")]
    internal static partial void NoticeRaised(ILogger logger, string key, string message);

    [LoggerMessage(
        EventId = 3421, Level = LogLevel.Debug,
        Message = "Notice acknowledged: {Key}")]
    internal static partial void NoticeAcknowledged(ILogger logger, string key);
}
