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
        _file = TryOpenFile(options, out var refusal);
        _console = options.Console ? console : null;
        DurableSinkRefusal = refusal;
    }

    /// <summary>
    /// Why the durable sink is absent, or null when it opened or was never
    /// asked for. The host decides where to report it.
    /// </summary>
    public string? DurableSinkRefusal { get; }

    /// <summary>Whether records are reaching a file, rather than only the ring.</summary>
    public bool HasDurableSink => _file is not null;

    /// <summary>
    /// Opens the rolling file, or answers null when the directory cannot be
    /// had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A diagnostics sink that cannot open must not stop the process.</b>
    /// Logs are diagnostics, not an operator channel (architecture 10 §3.1),
    /// and a backup product that refuses to run because it cannot write its own
    /// log file has its priorities inverted: the backup is the thing that
    /// matters, and it can be performed perfectly while the log goes nowhere.
    /// </para>
    /// <para>
    /// This is not hypothetical. <c>fallbackplan-agent install</c> is run by a
    /// person, before the service account exists, and its state directory
    /// points at a system location that person cannot create — so eagerly
    /// creating the log directory took down the one verb whose entire job is to
    /// set that account up, on every platform, with an access-denied stack
    /// trace and no clue as to why.
    /// </para>
    /// <para>
    /// Null is already the shape of "no durable sink" here, so degrading costs
    /// no new state. What it does cost is one claim elsewhere:
    /// <c>get_diagnostics</c> used to read <c>DurableSink</c> off the requested
    /// directory, which was the same thing as the truth only while opening it
    /// could not fail. It now reads <see cref="HasDurableSink"/>.
    /// </para>
    /// <para>
    /// Nothing is printed from here: this type does not know stdout from
    /// stderr, and one of its callers has a stdout that is a systemd unit file
    /// somebody is redirecting into a file. The reason is exposed instead, and
    /// the host picks the stream.
    /// </para>
    /// </remarks>
    private static RollingFileSink? TryOpenFile(LoggingOptions options, out string? refusal)
    {
        refusal = null;

        if (options.Directory is null)
        {
            return null;
        }

        try
        {
            return new RollingFileSink(options.Directory, options.MaximumFileBytes, options.RetainFiles);
        }
        catch (Exception failure)
            when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            refusal =
                $"diagnostics are not being written to '{options.Directory}': {failure.Message} "
                + "Records are still captured in memory and readable through the diagnostics verbs.";
            return null;
        }
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
