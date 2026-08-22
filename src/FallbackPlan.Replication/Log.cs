using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Replication;

/// <summary>
/// Replication diagnostics (ADR-0043; event ids 3000–3049).
/// </summary>
/// <remarks>
/// The counts these carry are the ones the copier actually computes. It never
/// totals the work up front — it streams from a listing, so a "{Bytes} to copy"
/// figure would have to be invented — and what it does know at the start is the
/// destination's opening inventory, which is exactly what makes a catch-up pass
/// cheap or expensive.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3000, Level = LogLevel.Information,
        Message = "Replicating to {Destination}: the destination already holds {Held} object(s)")]
    internal static partial void ReplicationStarting(ILogger logger, string destination, int held);

    [LoggerMessage(
        EventId = 3001, Level = LogLevel.Trace,
        Message = "Copied {Key}")]
    internal static partial void ObjectCopied(ILogger logger, ObjectKey key);

    // Not "copy failed": a put that fails throws and stops the pass, because a
    // re-run resumes from the destination's inventory and costs only the gap.
    // The genuinely silent failure here is the other half — a delete the
    // destination declined during convergence, which today is counted as
    // not-deleted and never mentioned, leaving a replica holding an object its
    // policy dropped with nothing to say why.
    [LoggerMessage(
        EventId = 3002, Level = LogLevel.Warning,
        Message = "Converging {Destination} did not delete {Key}: the store answered {Outcome}. "
            + "The replica still holds it; the next pass will try again")]
    internal static partial void ConvergeDeleteRefused(
        ILogger logger, string destination, ObjectKey key, DeleteOutcome outcome);

    [LoggerMessage(
        EventId = 3003, Level = LogLevel.Information,
        Message = "Replication to {Destination} finished ({Pass}): {Copied} copied, "
            + "{AlreadyHeld} already present, {Deleted} deleted")]
    internal static partial void ReplicationComplete(
        ILogger logger, string destination, string pass, long copied, long alreadyHeld, long deleted);
}
