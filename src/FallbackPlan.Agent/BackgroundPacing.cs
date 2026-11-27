using Bodu;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// The service's background limiters (NFR-PERF-013): one byte-rate limiter per
/// destination that declares a <c>transfer_limit</c> and one for the
/// installation's <c>background_read_limit</c> (ADR-0074), and one CPU limiter
/// for its <c>background_cpu_limit</c> (ADR-0200). A seam asks for its
/// <see cref="BackgroundPace"/>, which carries whichever of them apply.
/// </summary>
/// <remarks>
/// <para>
/// A limiter is the <em>shared</em> thing. Every background job that moves
/// bytes to or from a destination draws from that destination's one limiter,
/// so two syncs to one peer get the peer's rate between them. A limiter built
/// per job would give each the whole rate, and the link the operator capped
/// would carry twice what they wrote.
/// </para>
/// <para>
/// Limits are read afresh from the configuration the caller holds, as the
/// window is: an edited limit takes effect without a restart. A limiter is kept
/// while its limit's text is unchanged and replaced when the text changes, so
/// a job already paced at the old rate finishes on it while new work takes the
/// new one.
/// </para>
/// </remarks>
/// <param name="clock">What every limiter reads the time from and waits on.</param>
/// <param name="meter">What the CPU limiter reads the service's processor time from.</param>
internal sealed class BackgroundPacing(PacingClock clock, CpuMeter meter)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ByteRateLimiter> _destinations = new(StringComparer.Ordinal);
    private ByteRateLimiter? _sourceReads;
    private CpuLimiter? _cpu;

    /// <summary>
    /// What background work moving bytes to or from
    /// <paramref name="destination"/> is paced by: its transfer limit and the
    /// installation's CPU limit, or null when neither is set.
    /// </summary>
    public BackgroundPace? PaceFor(ClientConfiguration configuration, DestinationConfiguration destination) =>
        BackgroundPace.Of(ForDestination(destination), ForCpu(configuration));

    /// <summary>
    /// What a background capture's source reads are paced by: the read limit
    /// and the CPU limit, or null when neither is set.
    /// </summary>
    public BackgroundPace? PaceForSourceReads(ClientConfiguration configuration) =>
        BackgroundPace.Of(ForSourceReads(configuration), ForCpu(configuration));

    /// <summary>
    /// The CPU limiter for <paramref name="configuration"/>'s CPU limit, or
    /// null when it declares none. One for the installation, because the
    /// meter reads the whole service; kept while the limit's text is
    /// unchanged and replaced when it changes, as the byte limiters are.
    /// </summary>
    public CpuLimiter? ForCpu(ClientConfiguration configuration)
    {
        ThrowHelper.ThrowIfNull(configuration);
        var share = configuration.EffectiveBackgroundCpuLimit;
        lock (_gate)
        {
            if (share is null)
            {
                _cpu = null;
                return null;
            }

            if (_cpu is { } current && string.Equals(current.Share.Text, share.Text, StringComparison.Ordinal))
            {
                return current;
            }

            _cpu = new CpuLimiter(share, meter, clock);
            return _cpu;
        }
    }

    /// <summary>
    /// The limiter for <paramref name="destination"/>'s transfer limit, or null
    /// when it declares none.
    /// </summary>
    public ByteRateLimiter? ForDestination(DestinationConfiguration destination)
    {
        ThrowHelper.ThrowIfNull(destination);
        var rate = destination.EffectiveTransferLimit;
        lock (_gate)
        {
            if (rate is null)
            {
                _destinations.Remove(destination.Name);
                return null;
            }

            if (_destinations.TryGetValue(destination.Name, out var current)
                && string.Equals(current.Rate.Text, rate.Text, StringComparison.Ordinal))
            {
                return current;
            }

            var limiter = new ByteRateLimiter(rate, clock);
            _destinations[destination.Name] = limiter;
            return limiter;
        }
    }

    /// <summary>
    /// The limiter for <paramref name="configuration"/>'s source-read limit,
    /// or null when it declares none.
    /// </summary>
    public ByteRateLimiter? ForSourceReads(ClientConfiguration configuration)
    {
        ThrowHelper.ThrowIfNull(configuration);
        var rate = configuration.EffectiveBackgroundReadLimit;
        lock (_gate)
        {
            if (rate is null)
            {
                _sourceReads = null;
                return null;
            }

            if (_sourceReads is { } current && string.Equals(current.Rate.Text, rate.Text, StringComparison.Ordinal))
            {
                return current;
            }

            _sourceReads = new ByteRateLimiter(rate, clock);
            return _sourceReads;
        }
    }
}
