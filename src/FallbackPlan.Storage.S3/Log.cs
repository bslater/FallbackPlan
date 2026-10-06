using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Storage.S3;

/// <summary>
/// The S3-compatible store's diagnostics (ADR-0043; event ids 2700–2749).
/// </summary>
/// <remarks>
/// Keys are keyed identifiers, not paths. The endpoint, the bucket and the
/// access key id are never logged: they name an account at a provider, which
/// is the operator's business and nobody's log's.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2700, Level = LogLevel.Trace,
        Message = "Put {Key}: {Bytes} bytes")]
    internal static partial void ObjectPut(ILogger logger, ObjectKey key, long bytes);

    [LoggerMessage(
        EventId = 2701, Level = LogLevel.Trace,
        Message = "Get {Key}{Range}")]
    internal static partial void ObjectRead(ILogger logger, ObjectKey key, LogLabel range);

    [LoggerMessage(
        EventId = 2702, Level = LogLevel.Debug,
        Message = "Store request {Operation} was refused for the moment ({Reason}); attempt {Attempt} of {Attempts}, trying again")]
    internal static partial void Retrying(
        ILogger logger, LogLabel operation, LogLabel reason, int attempt, int attempts);

    [LoggerMessage(
        EventId = 2703, Level = LogLevel.Warning,
        Message = "Store operation {Operation} failed for {Key}: {Reason}")]
    internal static partial void OperationFailed(ILogger logger, LogLabel operation, ObjectKey key, string reason);

    [LoggerMessage(
        EventId = 2704, Level = LogLevel.Debug,
        Message = "Deleted {Key}")]
    internal static partial void ObjectDeleted(ILogger logger, ObjectKey key);
}
