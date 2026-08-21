using Bodu;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// The one provider the hosts install: it captures a record once and hands it
/// to every sink (ADR-0043 §6).
/// </summary>
/// <remarks>
/// One provider rather than three is deliberate. A record is captured as
/// structured state exactly once, and each sink then decides how to render it —
/// which is what keeps "plaintext locally, redacted on the wire" a single
/// decision taken in <see cref="LogRecordRenderer"/> rather than a rule each
/// sink might implement slightly differently.
/// </remarks>
public sealed class FallbackPlanLoggerProvider : ILoggerProvider
{
    private readonly LogRing _ring;
    private readonly RollingFileSink? _file;
    private readonly TextWriter? _console;
    private readonly LevelSwitch _levels;

    /// <summary>Creates the provider and opens its sinks.</summary>
    /// <param name="options">What to log and where to put it.</param>
    /// <param name="console">Where a foreground run writes, when one was asked for.</param>
    public FallbackPlanLoggerProvider(LoggingOptions options, TextWriter? console = null)
    {
        ThrowHelper.ThrowIfNull(options);

        _levels = new LevelSwitch(options);
        _ring = new LogRing(options.RingCapacity);
        _file = options.Directory is null
            ? null
            : new RollingFileSink(options.Directory, options.MaximumFileBytes, options.RetainFiles);
        _console = options.Console ? console : null;
    }

    /// <summary>The buffer a client reads from.</summary>
    public LogRing Ring => _ring;

    /// <summary>The live level rules, changeable without a restart.</summary>
    public LevelSwitch Levels => _levels;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() => _file?.Dispose();

    private void Write(LogRecord record)
    {
        var stored = _ring.Add(record);

        // Inside the trust boundary: the file and a foreground console both sit
        // on the machine that already holds the files, so both render in full.
        // Only what crosses the boundary is redacted, and that happens where
        // the record is served, not here (ADR-0043 section 4).
        if (_file is null && _console is null)
        {
            return;
        }

        // The sequence leads the line so a support log and a client's feed can
        // be lined up: both carry the same monotonic number, which is the only
        // thing they share once one of them is redacted.
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{stored.Sequence,-8}  " +
            $"{DateTimeOffset.FromUnixTimeMilliseconds(stored.TimestampUnixMilliseconds):u}  " +
            $"{LoggingOptions.NameOf(stored.Level),-11}  {stored.EventId,-5}  {stored.Category}  " +
            $"{LogRecordRenderer.Render(stored, RenderMode.Full)}");

        _file?.Write(line);
        _console?.WriteLine(line);
    }

    private sealed class CapturingLogger(FallbackPlanLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= owner._levels.LevelFor(category);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // The source generator's state is the name/value list. Keeping it
            // whole is what makes a second, redacted rendering possible later.
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? [new KeyValuePair<string, object?>(
                    LogRecord.OriginalFormatKey, formatter(state, exception))];

            owner.Write(new LogRecord(
                Sequence: 0,
                TimestampUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Level: logLevel,
                EventId: eventId.Id,
                Category: category,
                Values: values,
                ExceptionType: exception?.GetType().FullName,
                ExceptionMessage: exception?.Message));
        }
    }
}

/// <summary>
/// The level rules, swappable at runtime so a level change takes effect without
/// a restart (ADR-0043 §6, FR-SVC-010).
/// </summary>
public sealed class LevelSwitch(LoggingOptions options)
{
    private volatile LoggingOptions _options = options;

    /// <summary>The rules currently in force.</summary>
    public LoggingOptions Current => _options;

    /// <summary>The level in force for a category.</summary>
    /// <param name="category">The logger category to resolve.</param>
    public LogLevel LevelFor(string category) => _options.LevelFor(category);

    /// <summary>Replaces the rules in force.</summary>
    /// <param name="replacement">The new rules.</param>
    public void Set(LoggingOptions replacement)
    {
        ThrowHelper.ThrowIfNull(replacement);
        _options = replacement;
    }
}
