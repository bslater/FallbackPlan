using FallbackPlan.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Index;

/// <summary>
/// Index and journal diagnostics (ADR-0043; event ids 1700–1799).
/// </summary>
/// <remarks>
/// The journal's own signed records are the durable account of what happened
/// (specification 08 §6); these are the working notes beside them. The one
/// case that genuinely needs both is a sequence anomaly: the journal records
/// the facts, and this says loudly, at the time, that the facts disagreed.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1700, Level = LogLevel.Debug,
        Message = "Published index delta {DeltaId} at generation {Generation}: {Entries} entries")]
    internal static partial void DeltaPublished(
        ILogger logger, DeltaId deltaId, uint generation, int entries);

    [LoggerMessage(
        EventId = 1701, Level = LogLevel.Debug,
        Message = "Loaded index at generation {Generation}: {Deltas} deltas over checkpoint {CheckpointId}")]
    internal static partial void IndexLoaded(
        ILogger logger, uint generation, int deltas, CheckpointId checkpointId);

    [LoggerMessage(
        EventId = 1702, Level = LogLevel.Warning,
        Message = "An unparseable journal record was found and is being treated as live — "
            + "the collector will not reclaim what it cannot read")]
    internal static partial void UnparseableJournalRecord(ILogger logger);

    [LoggerMessage(
        EventId = 1703, Level = LogLevel.Critical,
        Message = "Writer {Writer} sequence anomaly: expected {Expected}, saw {Observed}. This is either a "
            + "stolen device key or two processes sharing a state directory — neither is a log line's "
            + "problem to solve")]
    internal static partial void SequenceAnomaly(
        ILogger logger, WriterId writer, ulong expected, ulong observed);

    [LoggerMessage(
        EventId = 1704, Level = LogLevel.Debug,
        Message = "Published a void delta for the unaccounted ordinal {Ordinal}")]
    internal static partial void VoidDeltaPublished(ILogger logger, ulong ordinal);
}
