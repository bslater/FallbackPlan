using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Segmentation;

/// <summary>
/// Segmentation diagnostics (ADR-0043; event ids 1400–1449).
/// </summary>
/// <remarks>
/// Trace only: this runs once per boundary, and a message per boundary would
/// be a message per few kilobytes of every file backed up. It exists for the
/// one question that cannot be answered any other way — why a file that
/// barely changed re-archived almost entirely.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1400, Level = LogLevel.Trace,
        Message = "Segmented {Bytes} bytes into {Segments} segments with {Profile}, mean {MeanBytes} bytes")]
    internal static partial void Segmented(
        ILogger logger, long bytes, int segments, string profile, long meanBytes);
}
