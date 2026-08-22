using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Storage.Local;

/// <summary>
/// The local object store's diagnostics (ADR-0043; event ids 2500–2549).
/// </summary>
/// <remarks>
/// Store operations are where a backup meets the disk, so this is where a
/// "the backup just stopped" report usually has to be answered from. Object
/// keys are keyed identifiers rather than paths, so they carry no filename —
/// but the store root is a real path and is declared as one.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2500, Level = LogLevel.Trace,
        Message = "Put {Key}: {Bytes} bytes")]
    internal static partial void ObjectPut(ILogger logger, ObjectKey key, long bytes);

    [LoggerMessage(
        EventId = 2501, Level = LogLevel.Trace,
        Message = "Get {Key}{Range}")]
    internal static partial void ObjectRead(ILogger logger, ObjectKey key, string range);

    [LoggerMessage(
        EventId = 2502, Level = LogLevel.Warning,
        Message = "Store operation {Operation} failed for {Key}: {Reason}")]
    internal static partial void OperationFailed(
        ILogger logger, string operation, ObjectKey key, string reason);

    [LoggerMessage(
        EventId = 2503, Level = LogLevel.Debug,
        Message = "Deleted {Key}")]
    internal static partial void ObjectDeleted(ILogger logger, ObjectKey key);
}
