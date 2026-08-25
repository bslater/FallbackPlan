using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Api;

/// <summary>
/// Command-transport diagnostics (ADR-0043; event ids 3600–3649).
/// </summary>
/// <remarks>
/// The transport is where a "the console cannot see the service" report has to
/// be answered from, and it is also where the ad-hoc <c>Action&lt;string&gt;</c>
/// log delegate used to write. Commands are named by verb only: a command's
/// arguments can carry a set name, a path, or a sealed envelope, and none of
/// those belong in a connection log.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3600, Level = LogLevel.Debug,
        Message = "Client connected: contract {ClientVersion}, service {ServiceVersion}, accepted {Accepted}")]
    internal static partial void ClientConnected(
        ILogger logger, string clientVersion, string serviceVersion, bool accepted);

    [LoggerMessage(
        EventId = 3601, Level = LogLevel.Warning,
        Message = "Refused a client at contract {ClientVersion}: this service speaks {ServiceVersion}")]
    internal static partial void VersionRefused(
        ILogger logger, string clientVersion, string serviceVersion);

    [LoggerMessage(
        EventId = 3602, Level = LogLevel.Debug,
        Message = "Command {Verb} answered {Result} in {ElapsedMs} ms")]
    internal static partial void CommandHandled(
        ILogger logger, string verb, string result, long elapsedMs);

    [LoggerMessage(
        EventId = 3603, Level = LogLevel.Warning,
        Message = "Connection failed: {Reason}")]
    internal static partial void ConnectionFailed(ILogger logger, string reason);

    // No subscriber count. The pump sees one connection; how many watchers
    // exist in total is the service's business and is not knowable from here,
    // and a count taken from the wrong side is worse than no count.
    [LoggerMessage(
        EventId = 3604, Level = LogLevel.Debug,
        Message = "This connection opened a progress watch; it now streams until the client leaves")]
    internal static partial void WatchOpened(ILogger logger);

    // A LogPath, not a string: a local endpoint IS a filesystem path, and the
    // service exposes no raw filesystem paths to a client (T-16). Declared as
    // a path, it renders in full in the machine's own log and as a digest in
    // anything a client reads.
    [LoggerMessage(
        EventId = 3605, Level = LogLevel.Debug,
        Message = "Listening on {Endpoint}")]
    internal static partial void Listening(ILogger logger, LogPath endpoint);

    // Information, not Debug: this line is the whole explanation for a command
    // that has no CommandHandled entry, and it replaces what used to appear
    // instead — a CommandHandled line hours late and a broken-pipe warning
    // that read as an unrelated failure (the 26,161,154 ms preview scan in the
    // 2026-08-25 log).
    [LoggerMessage(
        EventId = 3606, Level = LogLevel.Information,
        Message = "Command {Verb} abandoned after {ElapsedMs} ms: the client disconnected, so the work was cancelled")]
    internal static partial void CommandAbandoned(ILogger logger, string verb, long elapsedMs);
}
