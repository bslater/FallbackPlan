using FallbackPlan.Api;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// The diagnostics surface (ADR-0043 §6, FR-SVC-010): what is being logged,
/// what has been logged, and what to log next.
/// </summary>
/// <remarks>
/// <para>
/// All three answer from state already in hand — a level switch, a ring in
/// memory — so they are dispatched directly rather than queued behind a
/// ten-hour backup. Somebody asking why a backup is stuck must not have to wait
/// for it to finish to find out.
/// </para>
/// <para>
/// The local/remote split is the whole security design of this file, and it is
/// two separate rules rather than one. A remote caller <b>reads redacted</b>:
/// plaintext paths and correlatable identifiers stay on the machine that
/// already holds the files (ADR-0043 §4, NFR-PRIV-003). A remote caller
/// <b>may not set a level at all</b>: turning the logging up is a decision
/// about what this machine writes to its own disk, and ADR-0028 §6 already
/// draws that line around a paired console.
/// </para>
/// </remarks>
public sealed partial class ServiceCommandHandler
{
    /// <summary>
    /// The most records one page may carry. The frame cap is 8 MiB
    /// (<c>FrameCodec</c>); this is far below it, because the point of a
    /// ceiling is that no combination of record sizes can reach the cap, not
    /// that the usual case fits.
    /// </summary>
    private const int MaximumLogPage = 500;

    private RenderMode RenderFor => scope == CallerScope.Remote ? RenderMode.Redacted : RenderMode.Full;

    private ServiceResult GetDiagnostics()
    {
        if (runtime.Options.Logging is not { } logging)
        {
            // A runtime composed without logging — every test harness, and any
            // host that turned it off. Saying so beats inventing zeroes that
            // read like a service which logs nothing on purpose.
            return new ServiceError(
                ServiceErrorReason.Failed,
                "This service was started without a logging composition, so it has no diagnostics to report.");
        }

        var options = logging.Levels.Current;
        return new DiagnosticsResult(
            DefaultLevel: LogLevels.NameOf(options.Default),
            CategoryLevels: options.Categories.ToDictionary(
                pair => pair.Key, pair => LogLevels.NameOf(pair.Value), StringComparer.Ordinal),
            DurableSink: logging.DurableSink,
            RetainFiles: options.RetainFiles,
            MaximumFileBytes: options.MaximumFileBytes,
            RingCapacity: logging.Ring.Capacity,
            OldestSequence: logging.Ring.OldestSequence,
            NextSequence: logging.Ring.NextSequence);
    }

    private ServiceResult SetLogLevel(SetLogLevelCommand command)
    {
        if (scope == CallerScope.Remote)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                "Changing a log level is a local decision — a paired console may read this service's log "
                + "but not decide what goes into it (ADR-0028 §6).");
        }

        if (runtime.Options.Logging is not { } logging)
        {
            return new ServiceError(
                ServiceErrorReason.Failed,
                "This service was started without a logging composition, so there is no level to change.");
        }

        if (!LogLevels.TryParse(command.Level, out var level))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{command.Level}' is not a log level — the levels are {LogLevels.NameList()}.");
        }

        var current = logging.Levels.Current;
        var log = runtime.LoggerFor<ServiceCommandHandler>();
        var levelName = LogLevels.NameOf(level);

        if (command.Category is not { Length: > 0 } category)
        {
            logging.Levels.Set(current with { Default = level });
            Log.LogLevelChanged(log, "(default)", levelName);
            return new ConfigurationChangeResult(
                [$"The default log level is now {levelName}, until this service stops."]);
        }

        var categories = new Dictionary<string, LogLevel>(current.Categories, StringComparer.Ordinal)
        {
            [category] = level,
        };

        logging.Levels.Set(current with { Categories = categories });
        Log.LogLevelChanged(log, category, levelName);
        return new ConfigurationChangeResult(
        [
            $"'{category}' and everything beneath it now log at {levelName}, "
            + "until this service stops.",
            "The level in config.json is unchanged, so a restart puts it back — an afternoon of tracing "
            + "cannot become a machine that has been at trace for eight months.",
        ]);
    }

    private ServiceResult ReadLog(ReadLogCommand command)
    {
        if (runtime.Options.Logging is not { } logging)
        {
            return new ServiceError(
                ServiceErrorReason.Failed,
                "This service was started without a logging composition, so it holds no log to read.");
        }

        // Absent means everything the ring holds, which is Trace and below
        // nothing — not Information, which would quietly hide the level a
        // client most often turns the logging up to see.
        var minimum = LogLevel.Trace;
        if (command.MinimumLevel is { Length: > 0 } named && !LogLevels.TryParse(named, out minimum))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{named}' is not a log level — the levels are {LogLevels.NameList()}.");
        }

        // Clamped rather than refused: a client asking for more than a frame
        // can carry is asking for a page, not making an error, and answering
        // with a shorter page plus a cursor is the whole point of pagination.
        var wanted = Math.Clamp(command.MaxRecords, 1, MaximumLogPage);
        var page = logging.Ring.Read(Math.Max(command.SinceSequence, 0), wanted, minimum);
        var mode = RenderFor;

        var records = new List<LogRecordDescriptor>(page.Records.Count);
        foreach (var record in page.Records)
        {
            records.Add(new LogRecordDescriptor(
                record.Sequence,
                record.TimestampUnixMilliseconds,
                LogLevels.NameOf(record.Level),
                record.EventId,
                record.Category,
                LogRecordRenderer.Render(record, mode),
                record.ExceptionType,
                record.ExceptionMessage));
        }

        return new LogRecordsResult(records, page.NextSequence, page.Dropped);
    }
}
