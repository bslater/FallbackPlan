using Microsoft.Extensions.Logging;

namespace FallbackPlan.Recovery;

/// <summary>
/// Recovery-tool diagnostics (ADR-0043; event ids 3100–3149).
/// </summary>
/// <remarks>
/// The recovery tool runs on a clean machine, usually on the worst day
/// somebody has had in a while, and it must stay small. It keeps to the
/// console: no ring buffer, no rolling file, no provider package — the
/// abstraction and nothing more, which is what let the dependency clear
/// ADR-0019's format-critical bar in the first place.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 3100, Level = LogLevel.Information,
        Message = "Recovery kit read: repository format {FormatVersion}, {Destinations} destination(s)")]
    internal static partial void KitRead(ILogger logger, int formatVersion, int destinations);

    [LoggerMessage(
        EventId = 3101, Level = LogLevel.Information,
        Message = "Keys derived from the passphrase; the repository opens")]
    internal static partial void KeysDerived(ILogger logger);

    [LoggerMessage(
        EventId = 3102, Level = LogLevel.Warning,
        Message = "The passphrase does not reproduce this repository's keys")]
    internal static partial void PassphraseRefused(ILogger logger);

    [LoggerMessage(
        EventId = 3103, Level = LogLevel.Information,
        Message = "Recovery finished: {Restored} restored, {Failed} failed, {Skipped} skipped")]
    internal static partial void RecoveryComplete(ILogger logger, int restored, int failed, int skipped);
}
