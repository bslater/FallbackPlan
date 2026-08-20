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

    [LoggerMessage(
        EventId = 3604, Level = LogLevel.Debug,
        Message = "Progress watch opened; {Subscribers} now watching")]
    internal static partial void WatchOpened(ILogger logger, int subscribers);

    [LoggerMessage(
        EventId = 3605, Level = LogLevel.Debug,
        Message = "Listening on {Endpoint}")]
    internal static partial void Listening(ILogger logger, string endpoint);
}
