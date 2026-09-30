using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// One reading of how far this machine's clock stood from a peer's
/// (NFR-TIME-002, ADR-0077), taken from the only exchange that carries a
/// peer's clock: a replication receipt's signed <c>issued_at</c>
/// (peer-protocol 03 §3.5).
/// </summary>
/// <remarks>
/// A diagnostic, never an input to a decision (specification 00 §7): nothing
/// here corrects a clock or refuses a peer, it records how far two clocks
/// disagreed so that a machine with a badly wrong one can be found after the
/// fact. A disagreement does not say which clock is wrong.
/// </remarks>
/// <param name="SkewMilliseconds">
/// The peer's clock minus this one's. Positive: this clock was behind its peer.
/// </param>
/// <param name="ObservedAt">When the reading was complete, by this machine's clock.</param>
/// <param name="RoundTripMilliseconds">
/// How long the bracketed exchange took; half of it bounds how wrong
/// <paramref name="SkewMilliseconds"/> can be.
/// </param>
public sealed record ClockObservation(long SkewMilliseconds, ulong ObservedAt, ulong RoundTripMilliseconds)
{
    /// <summary>
    /// The oldest reading a capture records. A clock can be set right, or
    /// wrong, between a reading and a capture, and a stale reading recorded as
    /// if it were current would point the diagnosis at the wrong moment.
    /// </summary>
    public static TimeSpan MaximumAge { get; } = TimeSpan.FromDays(1);

    /// <summary>
    /// A reading from an exchange this side bracketed with its own clock: read
    /// just before the request went, and just after the answer came. The peer
    /// stamped <paramref name="issuedAt"/> between the two.
    /// </summary>
    /// <param name="sentAt">This clock, as the request went.</param>
    /// <param name="receivedAt">This clock, as the answer arrived.</param>
    /// <param name="issuedAt">The peer's signed stamp.</param>
    /// <returns>
    /// The reading, or null when this clock ran backwards across the exchange:
    /// a bracket that ends before it begins cannot hold a stamp, and what it
    /// would measure is the step.
    /// </returns>
    public static ClockObservation? FromExchange(ulong sentAt, ulong receivedAt, ulong issuedAt)
    {
        if (receivedAt < sentAt)
        {
            return null;
        }

        var roundTrip = receivedAt - sentAt;
        var midpoint = sentAt + (roundTrip / 2);
        return new ClockObservation(checked((long)issuedAt - (long)midpoint), receivedAt, roundTrip);
    }

    /// <summary>
    /// The reading a capture records: of its set's destinations', the freshest
    /// no older than <see cref="MaximumAge"/> and not dated in this clock's
    /// future — which a clock set back since the reading makes, and which then
    /// describes a clock this one no longer is. Between readings of the same
    /// moment, the tighter bracket.
    /// </summary>
    /// <param name="observations">Each destination's latest reading, null where it has none.</param>
    /// <param name="nowUnixMilliseconds">This clock, now.</param>
    /// <returns>The reading, or null when none qualifies.</returns>
    public static ClockObservation? ForCapture(IEnumerable<ClockObservation?> observations, ulong nowUnixMilliseconds)
    {
        ThrowHelper.ThrowIfNull(observations);

        var oldest = (ulong)MaximumAge.TotalMilliseconds;
        return observations
            .OfType<ClockObservation>()
            .Where(observation => observation.ObservedAt <= nowUnixMilliseconds
                && nowUnixMilliseconds - observation.ObservedAt <= oldest)
            .OrderByDescending(observation => observation.ObservedAt)
            .ThenBy(observation => observation.RoundTripMilliseconds)
            .FirstOrDefault();
    }
}
