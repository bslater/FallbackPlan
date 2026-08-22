using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// The local scanner's diagnostics (ADR-0043; event ids 2600–2649).
/// </summary>
/// <remarks>
/// This is the layer with the most to say and the most care needed in saying
/// it: almost everything it knows is a path. Every path here is declared
/// <see cref="LogPath"/>, so the local log names the file that could not be
/// read and the client feed does not (ADR-0043 §4).
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2600, Level = LogLevel.Debug,
        Message = "Scanning root {Root}")]
    internal static partial void ScanningRoot(ILogger logger, LogPath root);

    [LoggerMessage(
        EventId = 2601, Level = LogLevel.Trace,
        Message = "Entering {Directory}")]
    internal static partial void EnteringDirectory(ILogger logger, LogPath directory);

    [LoggerMessage(
        EventId = 2602, Level = LogLevel.Warning,
        Message = "Could not list {Directory} ({Reason}) — its subtree is not in this snapshot")]
    internal static partial void ListingFailed(ILogger logger, LogPath directory, string reason);

    [LoggerMessage(
        EventId = 2603, Level = LogLevel.Warning,
        Message = "Could not read {Path} ({Reason}) — recorded as a capture failure, the scan continues")]
    internal static partial void EntryFailed(ILogger logger, LogPath path, string reason);

    [LoggerMessage(
        EventId = 2604, Level = LogLevel.Debug,
        Message = "Scanned {Root}: {Files} files, {Directories} directories, {Failures} failures")]
    internal static partial void ScanComplete(
        ILogger logger, LogPath root, int files, int directories, int failures);

    [LoggerMessage(
        EventId = 2605, Level = LogLevel.Debug,
        Message = "Excluded by rule: {Path}")]
    internal static partial void Excluded(ILogger logger, LogPath path);
}
