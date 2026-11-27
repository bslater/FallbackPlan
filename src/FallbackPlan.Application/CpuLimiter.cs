using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// Holds background work to a <see cref="CpuShare"/> of the machine
/// (NFR-PERF-013, ADR-0200). One for the installation, shared by every
/// background job, because the <see cref="CpuMeter"/> reads the whole service:
/// a limiter per job would charge each of them what all of them spent.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="PaceAsync"/> is a look. It charges the processor time the
/// service spent since the previous look and waits whatever keeps the total
/// inside the share. The waiting is <see cref="ByteRateLimiter"/>'s virtual
/// scheduling with its one-second burst: the limiter keeps the instant at
/// which everything charged so far would have been spent at the share, and the
/// caller waits for whatever part of it lies more than a second ahead.
/// </para>
/// <para>
/// Two things differ from bytes, because processor time is measured after it
/// is spent rather than asked for before. What was spent since the last look
/// is spread over the time since that look, so a little housekeeping on an
/// idle service holds nothing back. And what the limiter owes is remembered
/// for <see cref="Memory"/> at most. A person's long restore is never paced,
/// and owing all of it would hold background work back long after the
/// restore ended.
/// </para>
/// <para>
/// A cancelled wait keeps its charge: unlike a byte that never moved, the
/// processor time was spent.
/// </para>
/// </remarks>
public sealed class CpuLimiter
{
    /// <summary>How much the limiter can owe: a wait never lasts more than this, less the burst.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromSeconds(10);

    private static readonly long BurstTicks = TimeSpan.TicksPerSecond;

    private readonly CpuMeter _meter;
    private readonly PacingClock _clock;
    private readonly long _shareOfTenThousand;
    private readonly Lock _gate = new();
    private long _finishesAt;
    private long _lookedAt;
    private long _processorTime;
    private long _charged;

    /// <summary>A limiter at <paramref name="share"/>, idle, charged nothing spent before it existed.</summary>
    /// <param name="share">The share of the machine to hold background work to.</param>
    /// <param name="meter">What to read the service's processor time from.</param>
    /// <param name="clock">What to read the time from and wait on.</param>
    public CpuLimiter(CpuShare share, CpuMeter meter, PacingClock clock)
    {
        ThrowHelper.ThrowIfNull(share);
        ThrowHelper.ThrowIfNull(meter);
        ThrowHelper.ThrowIfNull(clock);
        Share = share;
        _meter = meter;
        _clock = clock;
        _shareOfTenThousand = (long)share.Percent * 100 * meter.ProcessorCount;
        _finishesAt = _lookedAt = clock.NowTicks();
        _processorTime = meter.ProcessorTime().Ticks;
    }

    /// <summary>The share this limiter holds to.</summary>
    public CpuShare Share { get; }

    /// <summary>The processor time charged so far: what the service spent while this limiter was looking.</summary>
    public TimeSpan ProcessorTimeCharged => TimeSpan.FromTicks(Interlocked.Read(ref _charged));

    /// <summary>
    /// Charges what the service spent since the last look, then waits until
    /// that is inside the share.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait; what was spent stays charged.</param>
    public async ValueTask PaceAsync(CancellationToken cancellationToken)
    {
        long wait;
        lock (_gate)
        {
            var now = _clock.NowTicks();
            var spent = Math.Max(0, _meter.ProcessorTime().Ticks - _processorTime);
            _processorTime += spent;
            Interlocked.Add(ref _charged, spent);

            // Spent somewhere since the last look, so owed from then at the
            // earliest, and never owed more than the limiter remembers.
            var owedFrom = Math.Max(_finishesAt, _lookedAt);
            _lookedAt = now;
            _finishesAt = Math.Min(owedFrom + CostOf(spent), now + Memory.Ticks);
            wait = _finishesAt - now - BurstTicks;
        }

        if (wait > 0)
        {
            await _clock.DelayAsync(TimeSpan.FromTicks(wait), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The wall time <paramref name="spent"/> takes at the share, in ticks, rounded up.</summary>
    private long CostOf(long spent)
    {
        var ticks = (((Int128)spent * 10_000) + _shareOfTenThousand - 1) / _shareOfTenThousand;
        return ticks > long.MaxValue / 4 ? long.MaxValue / 4 : (long)ticks;
    }
}
