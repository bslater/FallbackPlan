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

    /// <summary>The features this build offers (02 §4).</summary>
    public static IReadOnlyList<string> SupportedFeatures { get; } =
        [DestinationVerificationFeature, RetentionInstructionFeature, TerminationNoticeFeature, RetrievalFeature];

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
