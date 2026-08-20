using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Format;

/// <summary>
/// Format-layer diagnostics (ADR-0043; event ids 1100–1149).
/// </summary>
/// <remarks>
/// Trace only, and deliberately few. This assembly's dependency closure is
/// the standalone recovery tool's, and the recovery tool's whole job is to
/// run on a clean machine with as little moving as possible. A codec that
/// narrated every record would also be a codec allocating a message per
/// record in the hot path — the level keeps it silent unless somebody has
/// deliberately gone looking.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1100, Level = LogLevel.Trace,
        Message = "Parsed {What} at format version {FormatVersion}")]
    internal static partial void Parsed(ILogger logger, string what, int formatVersion);

    [LoggerMessage(
        EventId = 1101, Level = LogLevel.Debug,
        Message = "Refused {What}: {Reason}")]
    internal static partial void Refused(ILogger logger, string what, string reason);

    [LoggerMessage(
        EventId = 1102, Level = LogLevel.Debug,
        Message = "Descriptor requires features this build does not implement: {Features}")]
    internal static partial void UnsupportedFeatures(ILogger logger, string features);
}
