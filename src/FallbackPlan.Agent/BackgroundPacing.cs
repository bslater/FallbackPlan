using Bodu;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// The service's byte-rate limiters (NFR-PERF-013, ADR-0074): one per
/// destination that declares a <c>transfer_limit</c>, and one for the
/// installation's <c>background_read_limit</c>.
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
internal sealed class BackgroundPacing(PacingClock clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ByteRateLimiter> _destinations = new(StringComparer.Ordinal);
    private ByteRateLimiter? _sourceReads;

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
