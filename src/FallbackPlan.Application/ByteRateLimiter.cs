using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// Paces bytes to one <see cref="ByteRate"/> (NFR-PERF-013, ADR-0074), shared
/// by everything that rate governs: every background job that reads or writes
/// one destination draws from that destination's limiter, so two syncs to one
/// peer get the peer's rate between them rather than each having it.
/// </summary>
/// <remarks>
/// <para>
/// A virtual-scheduling token bucket: the limiter keeps the instant at which
/// everything admitted so far would have finished at the rate, each
/// acquisition moves it on by its own cost, and the caller waits for whatever
/// of that lies more than one second ahead. The second is the burst — an idle
/// limiter lets a second's worth through at once, so a small object is never
/// delayed and a limit costs nothing until it is actually reached — and idle
/// time never banks more credit than that.
/// </para>
/// <para>
/// An acquisition whose wait is cancelled gives its cost back. Its bytes never
/// passed, and a parked capture or a stopping service must not leave a debt
/// that holds back the next job to use the limit.
/// </para>
/// </remarks>
public sealed class ByteRateLimiter
{
    private static readonly long BurstTicks = TimeSpan.TicksPerSecond;

    private readonly PacingClock _clock;
    private readonly Lock _gate = new();
    private long _finishesAt;
    private long _bytesPaced;

    /// <summary>A limiter at <paramref name="rate"/>, idle.</summary>
    /// <param name="rate">The rate to hold to.</param>
    /// <param name="clock">What to read the time from and wait on.</param>
    public ByteRateLimiter(ByteRate rate, PacingClock clock)
    {
        ThrowHelper.ThrowIfNull(rate);
        ThrowHelper.ThrowIfNull(clock);
        Rate = rate;
        _clock = clock;
        _finishesAt = clock.NowTicks();
    }

    /// <summary>The rate this limiter holds to.</summary>
    public ByteRate Rate { get; }

    /// <summary>The bytes admitted so far — what has actually passed, not what was asked for.</summary>
    public long BytesPaced => Interlocked.Read(ref _bytesPaced);

    /// <summary>
    /// Waits until <paramref name="bytes"/> more may pass at the rate, then
    /// counts them as passed.
    /// </summary>
    /// <param name="bytes">How many bytes are about to move, or just did.</param>
    /// <param name="cancellationToken">Ends the wait; the bytes are then not counted.</param>
    public async ValueTask AcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0)
        {
            return;
        }

        var cost = CostOf(bytes);
        long wait;
        lock (_gate)
        {
            var now = _clock.NowTicks();
            _finishesAt = Math.Max(_finishesAt, now) + cost;
            wait = _finishesAt - now - BurstTicks;
        }

        if (wait > 0)
        {
            try
            {
                await _clock.DelayAsync(TimeSpan.FromTicks(wait), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _finishesAt = Math.Max(_finishesAt - cost, _clock.NowTicks());
                }

                throw;
            }
        }

        Interlocked.Add(ref _bytesPaced, bytes);
    }

    /// <summary>The time <paramref name="bytes"/> take at the rate, in ticks, rounded up.</summary>
    private long CostOf(long bytes)
    {
        var ticks = (((Int128)bytes * TimeSpan.TicksPerSecond) + Rate.BytesPerSecond - 1) / Rate.BytesPerSecond;
        return ticks > long.MaxValue / 2 ? long.MaxValue / 2 : (long)ticks;
    }
}
