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

    // Not "Quarantined". The quarantine directory is where a restore lands by
    // default, for every item, and saying so per item would be noise. What is
    // worth a Warning is the item whose destination was already occupied, and
    // that has two distinct outcomes with two different answers to "where is my
    // file now" — the restored copy moved, or the live file did. One message
    // for both would have to be vague about which.
    [LoggerMessage(
        EventId = 2805, Level = LogLevel.Warning,
        Message = "Something was already at {Path}; the restored copy went beside it as {Landing}, "
            + "and the existing file was left untouched")]
    internal static partial void WroteBeside(ILogger logger, LogPath path, LogPath landing);

    [LoggerMessage(
        EventId = 2806, Level = LogLevel.Warning,
        Message = "Something was already at {Path}; it was moved to {Refuge} before the restore "
            + "wrote over it")]
    internal static partial void DisplacedExisting(ILogger logger, LogPath path, LogPath refuge);
}
