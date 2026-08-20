using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Replication;

/// <summary>
/// Replication diagnostics (ADR-0043; event ids 3000–3049).
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3000, Level = LogLevel.Information,
        Message = "Replicating to {Destination}: {Objects} objects, {Bytes} bytes to copy")]
    internal static partial void ReplicationStarting(ILogger logger, string destination, int objects, long bytes);

    [LoggerMessage(
        EventId = 3001, Level = LogLevel.Trace,
        Message = "Copied {Key}")]
    internal static partial void ObjectCopied(ILogger logger, ObjectKey key);

    [LoggerMessage(
        EventId = 3002, Level = LogLevel.Warning,
        Message = "Could not copy {Key} to {Destination}: {Reason}")]
    internal static partial void CopyFailed(ILogger logger, ObjectKey key, string destination, string reason);

    [LoggerMessage(
        EventId = 3003, Level = LogLevel.Information,
        Message = "Replication to {Destination} finished: {Copied} copied, {Failed} failed, {Skipped} already present")]
    internal static partial void ReplicationComplete(
        ILogger logger, string destination, int copied, int failed, int skipped);
}
