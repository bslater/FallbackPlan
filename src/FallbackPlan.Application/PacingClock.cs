using Bodu;
using System.Diagnostics;

namespace FallbackPlan.Application;

/// <summary>
/// The clock and the wait background pacing runs on (ADR-0074): a monotonic
/// reading in <see cref="TimeSpan"/> ticks, and a cancellable delay.
/// </summary>
/// <remarks>
/// Injected for the reason the background window takes its instant as an
/// argument: a test states the time rather than waiting for it. A virtual
/// clock whose waits complete at once proves a rate by the waits it was asked
/// for, where the real one would make every paced test take minutes.
/// </remarks>
public sealed class PacingClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    private readonly Func<long> _nowTicks;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>A clock over the given reading and wait.</summary>
    /// <param name="nowTicks">A monotonic reading, in <see cref="TimeSpan"/> ticks.</param>
    /// <param name="delay">Waits the given time, or throws when the token is cancelled.</param>
    public PacingClock(Func<long> nowTicks, Func<TimeSpan, CancellationToken, Task> delay)
    {
        ThrowHelper.ThrowIfNull(nowTicks);
        ThrowHelper.ThrowIfNull(delay);
        _nowTicks = nowTicks;
        _delay = delay;
    }

    /// <summary>The production clock: the monotonic stopwatch and a real delay.</summary>
    public static PacingClock System { get; } = new(
        static () => Stopwatch.GetElapsedTime(Origin).Ticks,
        static (wait, cancellationToken) => Task.Delay(wait, cancellationToken));

    /// <summary>The current reading, in <see cref="TimeSpan"/> ticks.</summary>
    public long NowTicks() => _nowTicks();

    /// <summary>Waits <paramref name="wait"/>, ending early with an exception when cancelled.</summary>
    public Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken) => _delay(wait, cancellationToken);
}
