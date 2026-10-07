using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>
/// The Azure Blob store's diagnostics (ADR-0043; event ids 2750–2799).
/// </summary>
/// <remarks>
/// Keys are keyed identifiers, not paths. The endpoint, the account, the
/// container and the request's address are never logged: they name an
/// account at a provider, and under a shared access signature the address
/// carries the signature itself.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2750, Level = LogLevel.Trace,
        Message = "Put blob {Key}: {Bytes} bytes")]
    internal static partial void ObjectPut(ILogger logger, ObjectKey key, long bytes);

    [LoggerMessage(
        EventId = 2751, Level = LogLevel.Trace,
        Message = "Get blob {Key}{Range}")]
    internal static partial void ObjectRead(ILogger logger, ObjectKey key, LogLabel range);

    [LoggerMessage(
        EventId = 2752, Level = LogLevel.Debug,
        Message = "Blob request {Operation} was refused for the moment ({Reason}); attempt {Attempt} of {Attempts}, trying again")]
    internal static partial void Retrying(
        ILogger logger, LogLabel operation, LogLabel reason, int attempt, int attempts);

    [LoggerMessage(
        EventId = 2753, Level = LogLevel.Warning,
        Message = "Blob operation {Operation} failed for {Key}: {Reason}")]
    internal static partial void OperationFailed(ILogger logger, LogLabel operation, ObjectKey key, string reason);

    [LoggerMessage(
        EventId = 2754, Level = LogLevel.Debug,
        Message = "Deleted blob {Key}")]
    internal static partial void ObjectDeleted(ILogger logger, ObjectKey key);
}
