using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Catalogue;

/// <summary>
/// Catalogue diagnostics (ADR-0043; event ids 1800–1849).
/// </summary>
/// <remarks>
/// The catalogue is a disposable cache, so its failures are never correctness
/// failures — which is exactly why they go unnoticed. A rebuild that happens
/// every night because something keeps invalidating it costs real time and
/// shows up nowhere else.
/// </remarks>
internal static partial class Log
{
    // The generation this used to carry is not known at Open: the catalogue
    // is opened before anything reads the descriptor. What is known, and is
    // the fact worth having, is whether the cache on disk survived — a
    // catalogue recreated on every open is a nightly rebuild nobody sees.
    [LoggerMessage(
        EventId = 1800, Level = LogLevel.Debug,
        Message = "Catalogue opened for {Repository}; the cache on disk was {Disposition}")]
    internal static partial void CatalogueOpened(ILogger logger, RepositoryId repository, string disposition);

    // No repository id and no reason: a rebuilder holds a loader, not a
    // repository, and the reason lives with the caller that decided to
    // rebuild. The generation it rebuilds against is what this level answers.
    [LoggerMessage(
        EventId = 1801, Level = LogLevel.Information,
        Message = "Rebuilding the catalogue from checkpoint plus deltas at generation {Generation}")]
    internal static partial void RebuildStarting(ILogger logger, ulong generation);

    [LoggerMessage(
        EventId = 1802, Level = LogLevel.Information,
        Message = "Catalogue rebuild finished: {Entries} entries, {Checkpoints} checkpoints, "
            + "{Deltas} deltas, {Findings} findings")]
    internal static partial void RebuildComplete(
        ILogger logger, int entries, int checkpoints, int deltas, int findings);

    [LoggerMessage(
        EventId = 1803, Level = LogLevel.Warning,
        Message = "Catalogue damage finding: {Kind} — {Detail}")]
    internal static partial void DamageFound(ILogger logger, DamageKind kind, string detail);
}
