using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.TestSupport;

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told, so a suite can assert
/// on diagnostics the way <c>TelemetryPrivacyTests</c> asserts on measurements
/// (ADR-0043).
/// </summary>
/// <remarks>
/// The structured state is kept whole rather than flattened to a sentence.
/// That is what lets a test check the redaction rule the way the product
/// applies it — by declared type — instead of grepping rendered text for
/// things that look like secrets, which is the failure mode NFR-SEC-006 names.
/// </remarks>
public sealed class RecordingLogger : ILogger, ILoggerProvider, ILoggerFactory
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();

    /// <summary>The minimum level this logger reports as enabled.</summary>
    public LogLevel Minimum { get; set; } = LogLevel.Trace;

    /// <summary>Everything logged so far, in order.</summary>
    public IReadOnlyList<RecordedLog> Records => [.. _records];

    /// <summary>Every value ever logged, across all records.</summary>
    public IEnumerable<object?> AllValues =>
        _records.SelectMany(record => record.Values).Select(pair => pair.Value);

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear() => _records.Clear();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= Minimum;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];

        _records.Enqueue(new RecordedLog(
            logLevel,
            eventId.Id,
            eventId.Name ?? string.Empty,
            formatter(state, exception),
            values,
            exception));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // One logger records everything; there is nothing to fan out to.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing held.
    }
}

/// <summary>One thing a component logged.</summary>
/// <param name="Level">Its severity.</param>
/// <param name="EventId">The stable id from its <c>[LoggerMessage]</c> declaration.</param>
/// <param name="EventName">The generated event name, normally the method name.</param>
/// <param name="Message">The message as the default formatter renders it.</param>
/// <param name="Values">The template's named holes and their values, as logged.</param>
/// <param name="Exception">The exception attached, when there was one.</param>
public sealed record RecordedLog(
    LogLevel Level,
    int EventId,
    string EventName,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Values,
    Exception? Exception)
{
    /// <summary>The value logged under <paramref name="name"/>, or null if absent.</summary>
    /// <param name="name">The template hole's name.</param>
    public object? Value(string name) =>
        Values.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;
}
