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

    // Declared naming a checkpoint id, which a Checkpoint does not carry — it
    // records only its predecessor. Reporting the counts instead says as much
    // about the load's shape, and adds the number of damage findings, which is
    // the figure worth seeing beside it.
    [LoggerMessage(
        EventId = 1701, Level = LogLevel.Debug,
        Message = "Loaded index at generation {Generation}: {Deltas} deltas over {Checkpoints} checkpoints, "
            + "{Findings} damage findings")]
    internal static partial void IndexLoaded(
        ILogger logger, uint generation, int deltas, int checkpoints, int findings);

    [LoggerMessage(
        EventId = 1702, Level = LogLevel.Warning,
        Message = "An unparseable journal record was found and is being treated as live — "
            + "the collector will not reclaim what it cannot read")]
    internal static partial void UnparseableJournalRecord(ILogger logger);

    // Declared with an expected/observed pair, which is not the shape the
    // detection actually has. What the journal catches is a freshly allocated
    // sequence whose key is ALREADY occupied by a different record — the
    // sequence state regressed and another run wrote there (08 §2). There is
    // no "expected" number to report, only the one that collided, so the
    // message says what happened rather than inventing a second figure.
    [LoggerMessage(
        EventId = 1703, Level = LogLevel.Critical,
        Message = "Writer {Writer} allocated sequence {Sequence} and the store already holds a different "
            + "record there. The sequence state regressed: either a stolen device key or two processes "
            + "sharing a state directory — neither is a log line's problem to solve (T-18)")]
    internal static partial void SequenceAnomaly(ILogger logger, WriterId writer, ulong sequence);

    [LoggerMessage(
        EventId = 1704, Level = LogLevel.Debug,
        Message = "Published a void delta for the unaccounted ordinal {Ordinal}")]
    internal static partial void VoidDeltaPublished(ILogger logger, ulong ordinal);
}
