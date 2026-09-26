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
    // Debug, deliberately: the scheduler reads through this path every pass,
    // and at Information this one message was nearly the whole of the tier an
    // operator reads, saying each time that nothing had happened. The record
    // stays — "what was in force when this ran" must remain answerable — at
    // the level of routine mechanics. The Information-worthy event is change,
    // and only a host that remembers the previous load can see change, so the
    // Agent's runtime announces that (event 3742).
    [LoggerMessage(
        EventId = 3400, Level = LogLevel.Debug,
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

    // 3410, 3411 and 3412 were declared here and have moved or gone.
    //
    // Due-ness is decided by the Agent's Scheduler, not by this assembly, and
    // a project's Log class is internal to it — so the two due/not-due
    // messages now live in the Agent's range beside the pass that emits them.
    //
    // 3412 reported how many missed runs were coalesced into one catch-up. It
    // cannot be wired, because nothing counts them: Schedule.IsDue coalesces
    // *structurally* — "is a run due" is one boolean comparison, so five
    // slept-through occurrences owe exactly one run and are never enumerated
    // (ADR-0027 §1). Reporting the number would mean adding a computation for
    // no reason but to have something to log.

    [LoggerMessage(
        EventId = 3420, Level = LogLevel.Information,
        Message = "Notice raised: {Key} — {Message}")]
    internal static partial void NoticeRaised(ILogger logger, string key, string message);

    [LoggerMessage(
        EventId = 3421, Level = LogLevel.Debug,
        Message = "Notice acknowledged: {Key}")]
    internal static partial void NoticeAcknowledged(ILogger logger, string key);
}
