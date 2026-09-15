using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// Version selection and feature negotiation
/// (specification peer-protocol 02 §3–§4).
/// </summary>
/// <remarks>
/// <para>
/// Both sides run this over the same pair of hellos and must reach the same
/// answer without exchanging another message. That is why nothing here depends
/// on which side is calling: the version is a function of the two ranges, and
/// the feature set is a set intersection put in a fixed order.
/// </para>
/// <para>
/// Nothing here consults the agent version. 02 §2 forbids branching on it, and
/// the reason is worth restating at the place where someone would be tempted:
/// a protocol that sniffs versions acquires compatibility rules no specification
/// can then describe, and it acquires them one bug fix at a time.
/// </para>
/// </remarks>
public static class PeerSessionNegotiation
{
    /// <summary>The protocol version this build speaks (02 §3).</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>The oldest protocol version this build speaks.</summary>
    public const ushort OldestSupportedVersion = 1;

    /// <summary>A peer that offers this understands <see cref="PeeringTermination"/> (01 §3).</summary>
    public const string TerminationNoticeFeature = "termination-notice";

    /// <summary>The peer answers keyed random-range challenges (04).</summary>
    public const string DestinationVerificationFeature = "destination-verification";

    /// <summary>A peer that offers this accepts <see cref="RetentionOffer"/> within its floor (06).</summary>
    public const string RetentionInstructionFeature = "retention-instruction";

    /// <summary>An owner may read its replica back over the session (07; ADR-0041).</summary>
    public const string RetrievalFeature = "retrieval";

    /// <summary>
    /// A peer that offers this requires every <see cref="RetentionOffer"/> page
    /// to carry a reclaim signature it can verify
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5; 06 §3).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RetentionInstructionFeature"/> rather than
    /// folded into it, because the two say different things: one is "I accept
    /// deletion instructions at all", the other is "and I will not act on one
    /// that is not signed". A destination offering both refuses an unsigned
    /// instruction; a commander whose repository publishes no reclaim key
    /// simply never gets the second into the intersection, and is told what is
    /// missing instead of being refused mid-exchange.
    /// </remarks>
    public const string SignedRetentionFeature = "signed-retention";

    /// <summary>
    /// The peer verifies a retention signature over the session's identifier
    /// as well as the page ([02 §3.5](../../specifications/peer-protocol/02-session.md);
    /// [06 §4.1](../../specifications/peer-protocol/06-retention.md#41-retentionoffer)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It tells a <b>commander how to sign</b> and gates no check. A spoke
    /// holding a reclaim key requires the bound signature whatever the hello
    /// said, because a check the sender can opt out of is not a check
    /// (02 §6); what the feature buys is that a current commander talking to
    /// an older spoke signs the encoding that spoke can verify, instead of
    /// having every page refused.
    /// </para>
    /// <para>
    /// The other direction has no such kindness and cannot: an older commander
    /// signs the unbound encoding and a current spoke will not accept it,
    /// because accepting both is accepting the replayable one. Its retention
    /// is refused, loudly and by name, until it is upgraded — which costs a
    /// deletion not made, never a backup not taken.
    /// </para>
    /// </remarks>
    public const string SessionBoundRetentionFeature = "session-bound-retention";

    /// <summary>
    /// Both sides understand a transfer that begins part-way through an object
    /// ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md); 03 §5).
    /// </summary>
    /// <remarks>
    /// It gates both halves at once — the destination declaring what it part
    /// holds, and the source beginning an object at a non-zero offset —
    /// because either half alone is a protocol error to the other side: an
    /// older destination reads a chunk at a non-zero offset as
    /// <see cref="PeerRefusalReason.Malformed"/>, correctly, since without the
    /// agreement there is nothing it could mean.
    /// </remarks>
    public const string PartialObjectResumeFeature = "partial-object-resume";

    /// <summary>The features this build offers (02 §4).</summary>
    public static IReadOnlyList<string> SupportedFeatures { get; } =
    [
        DestinationVerificationFeature,
        RetentionInstructionFeature,
        SignedRetentionFeature,
        SessionBoundRetentionFeature,
        TerminationNoticeFeature,
        RetrievalFeature,
        PartialObjectResumeFeature,
    ];

    /// <summary>Builds the hello this build sends.</summary>
    /// <param name="agentVersion">Informational build string.</param>
    /// <param name="terms">Terms, when this side is the destination (01 §4).</param>
    /// <param name="required">Features this side requires of the peer.</param>
    /// <param name="offered">
    /// What this endpoint offers, defaulting to everything this build supports.
    /// The offered set is a property of the endpoint rather than a global
    /// constant (02 §4: "each side offers what it supports"), and narrowing it
    /// is the only way to stand an older peer in front of this one without
    /// checking out an older build — which is what the compatibility tests do
    /// with it. Nothing in production passes anything but the default.
    /// </param>
    /// <returns>The hello.</returns>
    public static SessionHello Hello(
        string agentVersion,
        PeerTerms? terms = null,
        IReadOnlyList<string>? required = null,
        IReadOnlyList<string>? offered = null) =>
        new(OldestSupportedVersion, CurrentVersion, offered ?? SupportedFeatures, required ?? [], agentVersion, terms);

    /// <summary>Negotiates a session from two hellos.</summary>
    /// <param name="ours">The hello this side sent.</param>
    /// <param name="theirs">The hello the peer sent.</param>
    /// <returns>The acceptance to send, which is also what the peer will compute.</returns>
    /// <exception cref="PeerProtocolException">
    /// The ranges do not overlap (<see cref="PeerRefusalReason.VersionUnsupported"/>),
    /// or a required feature is absent (<see cref="PeerRefusalReason.FeatureUnsupported"/>).
    /// </exception>
    public static SessionAccept Negotiate(SessionHello ours, SessionHello theirs)
    {
        ThrowHelper.ThrowIfNull(ours);
        ThrowHelper.ThrowIfNull(theirs);

        return new SessionAccept(SelectVersion(ours, theirs), SelectFeatures(ours, theirs));
    }

    /// <summary>Selects the highest version both sides speak (02 §3).</summary>
    /// <param name="ours">The hello this side sent.</param>
    /// <param name="theirs">The hello the peer sent.</param>
    /// <returns>The selected version.</returns>
    /// <exception cref="PeerProtocolException">The ranges do not overlap.</exception>
    public static ushort SelectVersion(SessionHello ours, SessionHello theirs)
    {
        ThrowHelper.ThrowIfNull(ours);
        ThrowHelper.ThrowIfNull(theirs);

        var highest = Math.Min(ours.MaximumVersion, theirs.MaximumVersion);
        var lowest = Math.Max(ours.MinimumVersion, theirs.MinimumVersion);

        if (highest < lowest)
        {
            // Both ranges, per 02 §3. A bare "unsupported" leaves the operator
            // with no way to tell which side needs upgrading, which is the one
            // question this refusal exists to answer.
            throw new PeerProtocolException(
                PeerRefusalReason.VersionUnsupported,
                $"This device speaks protocol versions {ours.MinimumVersion}–{ours.MaximumVersion} "
                + $"and was offered {theirs.MinimumVersion}–{theirs.MaximumVersion}.");
        }

        return (ushort)highest;
    }

    /// <summary>Intersects the offered sets and checks both required sets (02 §4).</summary>
    /// <param name="ours">The hello this side sent.</param>
    /// <param name="theirs">The hello the peer sent.</param>
    /// <returns>The features in effect, ordinal-sorted.</returns>
    /// <exception cref="PeerProtocolException">A required feature is absent.</exception>
    public static IReadOnlyList<string> SelectFeatures(SessionHello ours, SessionHello theirs)
    {
        ThrowHelper.ThrowIfNull(ours);
        ThrowHelper.ThrowIfNull(theirs);

        var weOffer = new HashSet<string>(ours.FeaturesOffered, StringComparer.Ordinal);
        var theyOffer = new HashSet<string>(theirs.FeaturesOffered, StringComparer.Ordinal);

        RequireAll(ours.FeaturesRequired, theyOffer, "The peer does not offer");

        // Their requirements are checked here too, though they will check them
        // themselves. Refusing before opening a session we know they will refuse
        // costs one message and saves a half-open state 02 §6 says must not exist.
        RequireAll(theirs.FeaturesRequired, weOffer, "This device does not offer");

        // Sorted, because both sides compute this independently and a set with
        // no defined order is a set the two sides can disagree about while both
        // being right.
        var effective = weOffer.Intersect(theyOffer, StringComparer.Ordinal).ToList();
        effective.Sort(StringComparer.Ordinal);
        return effective;
    }

    private static void RequireAll(IReadOnlyList<string> required, HashSet<string> offered, string complaint)
    {
        foreach (var feature in required)
        {
            if (!offered.Contains(feature))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.FeatureUnsupported, $"{complaint} the required feature '{feature}'.");
            }
        }
    }
}
