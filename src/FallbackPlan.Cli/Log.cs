using Microsoft.Extensions.Logging;

namespace FallbackPlan.Cli;

/// <summary>
/// Console diagnostics (ADR-0043; event ids 4000–4099): what a direct-mode
/// command noticed about the repository it opened.
/// </summary>
/// <remarks>
/// These two began as <c>Console.Error.WriteLine</c> calls inside
/// <see cref="CliSession"/> — the one place in the CLI that reached past the
/// writers it was handed and wrote to global console state, which is both a
/// testing hazard and a category error. They are diagnostics: facts about the
/// repository that belong in a log a support engineer reads, not results of
/// the command the operator ran. They keep reaching the operator because the
/// console's sink shows warnings by default (see <c>CliApplication</c>).
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 4000, Level = LogLevel.Warning,
        Message = "Repository {RepositoryId} was written under an UNSTABLE format version — it may become "
            + "unreadable by future releases (specification 01 §3.2)")]
    internal static partial void UnstableFormat(ILogger logger, Domain.Identifiers.RepositoryId repositoryId);

    [LoggerMessage(
        EventId = 4001, Level = LogLevel.Warning,
        Message = "Repository {RepositoryId} has stored KDF parameters below current creation minimums "
            + "(specification 03 §2)")]
    internal static partial void KdfBelowMinimums(ILogger logger, Domain.Identifiers.RepositoryId repositoryId);
}
