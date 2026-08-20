using Bodu;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// What a host calls to get a logger factory (ADR-0043 §1).
/// </summary>
/// <remarks>
/// Composition here is a method, not a container. That matches how everything
/// else in this codebase is wired — manual constructor injection — and it is
/// why ADR-0033's refusal of the Generic Host still stands: what is adopted is
/// the logging abstraction and two small sinks, not a hosting model.
/// </remarks>
public sealed class LoggingComposition : IDisposable
{
    private readonly ILoggerFactory _factory;
    private readonly FallbackPlanLoggerProvider _provider;

    private LoggingComposition(ILoggerFactory factory, FallbackPlanLoggerProvider provider)
    {
        _factory = factory;
        _provider = provider;
    }

    /// <summary>The factory to hand to whatever the host composes.</summary>
    public ILoggerFactory Factory => _factory;

    /// <summary>The buffer the diagnostics verbs read from.</summary>
    public LogRing Ring => _provider.Ring;

    /// <summary>The live level rules, so a level change needs no restart.</summary>
    public LevelSwitch Levels => _provider.Levels;

    /// <summary>Builds a factory over the ring, the rolling file, and optionally the console.</summary>
    /// <param name="options">What to log and where to put it.</param>
    /// <param name="console">Where a foreground run writes, when one was asked for.</param>
    public static LoggingComposition Create(LoggingOptions options, TextWriter? console = null)
    {
        ThrowHelper.ThrowIfNull(options);

        var provider = new FallbackPlanLoggerProvider(options, console);

        // The provider does its own level check against the live switch, so the
        // factory is left wide open: routing a level change through
        // LoggerFilterOptions would mean rebuilding the factory to change a
        // level, which is the restart this design exists to avoid.
        var factory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });

        return new LoggingComposition(factory, provider);
    }

    /// <summary>
    /// A composition that logs nowhere, for a host that was told not to and for
    /// tests that do not care.
    /// </summary>
    public static LoggingComposition None() =>
        Create(new LoggingOptions { Default = LogLevel.None, Directory = null, Console = false });

    /// <inheritdoc />
    public void Dispose()
    {
        _factory.Dispose();
        _provider.Dispose();
    }
}
