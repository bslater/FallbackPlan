using Microsoft.Extensions.Logging;

namespace FallbackPlan.Retention;

/// <summary>
/// Retention and collection diagnostics (ADR-0043; event ids 2900–2949).
/// </summary>
/// <remarks>
/// Deletion is the one thing a backup product does that cannot be undone, so
/// what was reclaimed — and what was deliberately spared — belongs in a record
/// that outlives the run. These are diagnostics, not the audit trail: spec 08
/// §6's signed journal records remain the durable account of destructive
/// operations, and nothing here replaces them.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2900, Level = LogLevel.Information,
        Message = "Retention planning for set {SetName}: {Snapshots} snapshots considered")]
    internal static partial void PlanningRetention(ILogger logger, string setName, int snapshots);

    [LoggerMessage(
        EventId = 2901, Level = LogLevel.Information,
        Message = "Retention plan for {SetName}: {Keep} kept, {Retire} to retire")]
    internal static partial void RetentionPlanned(ILogger logger, string setName, int keep, int retire);

    [LoggerMessage(
        EventId = 2902, Level = LogLevel.Warning,
        Message = "Retention held back for {SetName}: {Reason} — nothing was deleted")]
    internal static partial void RetentionHeld(ILogger logger, string setName, string reason);

    [LoggerMessage(
        EventId = 2903, Level = LogLevel.Information,
        Message = "Collection complete: {Deleted} objects deleted, {Reclaimed} bytes reclaimed, {Spared} spared")]
    internal static partial void CollectionComplete(
        ILogger logger, int deleted, long reclaimed, int spared);
}
