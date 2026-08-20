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
    [LoggerMessage(
        EventId = 1800, Level = LogLevel.Debug,
        Message = "Catalogue opened for {Repository} at generation {Generation}")]
    internal static partial void CatalogueOpened(ILogger logger, RepositoryId repository, uint generation);

    [LoggerMessage(
        EventId = 1801, Level = LogLevel.Information,
        Message = "Rebuilding the catalogue for {Repository}: {Reason}")]
    internal static partial void RebuildStarting(ILogger logger, RepositoryId repository, string reason);

    [LoggerMessage(
        EventId = 1802, Level = LogLevel.Information,
        Message = "Catalogue rebuild finished: {Entries} entries, {Findings} findings")]
    internal static partial void RebuildComplete(ILogger logger, int entries, int findings);

    [LoggerMessage(
        EventId = 1803, Level = LogLevel.Warning,
        Message = "Catalogue damage finding: {Kind} — {Detail}")]
    internal static partial void DamageFound(ILogger logger, string kind, string detail);
}
