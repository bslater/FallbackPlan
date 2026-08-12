using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// A destination's terms for storing another device's objects
/// (specification peer-protocol 01 §4).
/// </summary>
/// <remarks>
/// <para>
/// These are set by the side that will hold the objects, and travel with the
/// grant. A source may operate under narrower terms of its own choosing and may
/// never request more generous ones — this is a property of who owns the disk,
/// not a negotiation. The person lending space decides how much.
/// </para>
/// <para>
/// The storage path is deliberately absent. It is the destination's business,
/// and a source that knew it would be a source that could name it.
/// </para>
/// </remarks>
/// <param name="QuotaBytes">How many bytes the destination will hold.</param>
/// <param name="ScheduleWindow">
/// When transfers may run, in the ADR-0027 schedule expression language. Empty
/// means any time.
/// </param>
/// <param name="RetentionFloorGenerations">
/// How many generations the destination keeps regardless of what the source's
/// own retention policy asks for.
/// </param>
public sealed record PeerTerms(ulong QuotaBytes, string ScheduleWindow, uint RetentionFloorGenerations)
{
    /// <summary>
    /// Terms declaring no limits: quota 0 is "no byte ceiling declared", not
    /// "store nothing" (05 §1) — the unconfigured household default must not
    /// be a refusal of everything. A destination that means "nothing" does
    /// not pair, or revokes.
    /// </summary>
    public static PeerTerms None { get; } = new(0, string.Empty, 0);

    /// <summary>
    /// Whether these terms are within <paramref name="offered"/> — that is,
    /// whether a source asking for them is asking for no more than it was given.
    /// </summary>
    /// <param name="offered">What the destination offered.</param>
    /// <returns><see langword="true"/> when nothing here exceeds the offer.</returns>
    /// <remarks>
    /// A larger retention floor is <b>not</b> an excess. The floor is the
    /// destination's promise to keep things, so asking it to keep fewer
    /// generations than it offered is asking for less, and asking for more is
    /// asking the destination to do something it did not agree to.
    /// </remarks>
    public bool IsWithin(PeerTerms offered)
    {
        ThrowHelper.ThrowIfNull(offered);

        // Quota 0 is "no ceiling" (05 §1), so an unbounded offer admits any
        // request, and asking for "unbounded" exceeds every stated ceiling.
        var quotaWithin = offered.QuotaBytes == 0
            || (QuotaBytes > 0 && QuotaBytes <= offered.QuotaBytes);

        return quotaWithin && RetentionFloorGenerations <= offered.RetentionFloorGenerations;
    }

    /// <summary>
    /// Whether <paramref name="previous"/> has been narrowed by these terms.
    /// </summary>
    /// <param name="previous">The terms in force before.</param>
    /// <returns><see langword="true"/> when anything a source relies on shrank.</returns>
    /// <remarks>
    /// A destination may change its terms at any time and the source continues
    /// under the new ones. Where they narrow, the source reports the affected
    /// backup set as degraded rather than quietly failing to replicate: an
    /// unexplained stall is the failure mode architecture 09 §2 calls out, and
    /// "your friend reduced the space they are lending you" is a fact the person
    /// relying on it needs told.
    /// </remarks>
    public bool Narrows(PeerTerms previous)
    {
        ThrowHelper.ThrowIfNull(previous);

        // Quota 0 is "no ceiling" (05 §1): a ceiling appearing where none was
        // is a narrowing, a ceiling lifted is not, and between two ceilings
        // the smaller narrows.
        var quotaNarrowed = QuotaBytes > 0
            && (previous.QuotaBytes == 0 || QuotaBytes < previous.QuotaBytes);

        return quotaNarrowed
            || RetentionFloorGenerations < previous.RetentionFloorGenerations
            || (previous.ScheduleWindow.Length == 0 && ScheduleWindow.Length > 0);
    }
}
