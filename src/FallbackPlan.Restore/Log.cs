using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Restore;

/// <summary>
/// The restore engine's diagnostics (ADR-0043; event ids 2800–2849).
/// </summary>
/// <remarks>
/// A restore is the moment the product is judged, and a partial one is the
/// hardest thing to explain after the fact: the receipt records every item's
/// outcome, but not the reasoning that produced it. These fill that gap.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2800, Level = LogLevel.Information,
        Message = "Restore starting: {Items} items to {Destination}, overwrite {Overwrite}")]
    internal static partial void RestoreStarting(
        ILogger logger, int items, LogPath destination, string overwrite);

    [LoggerMessage(
        EventId = 2801, Level = LogLevel.Debug,
        Message = "Restored {Path}: {Bytes} bytes")]
    internal static partial void ItemRestored(ILogger logger, LogPath path, ulong bytes);

    [LoggerMessage(
        EventId = 2802, Level = LogLevel.Warning,
        Message = "Could not restore {Path}: {Reason}")]
    internal static partial void ItemFailed(ILogger logger, LogPath path, string reason);

    [LoggerMessage(
        EventId = 2803, Level = LogLevel.Debug,
        Message = "Skipped {Path}: {Reason}")]
    internal static partial void ItemSkipped(ILogger logger, LogPath path, string reason);

    [LoggerMessage(
        EventId = 2804, Level = LogLevel.Information,
        Message = "Restore {Outcome}: {Restored} restored, {Failed} failed, {Skipped} skipped")]
    internal static partial void RestoreComplete(
        ILogger logger, RestoreOutcome outcome, int restored, int failed, int skipped);

    [LoggerMessage(
        EventId = 2805, Level = LogLevel.Warning,
        Message = "Quarantined {Path}: {Reason} — written beside rather than over")]
    internal static partial void Quarantined(ILogger logger, LogPath path, string reason);
}
