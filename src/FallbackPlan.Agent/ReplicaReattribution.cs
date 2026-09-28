using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The operator's re-attribution
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// §3): which paired device a replica stored here belongs to, re-pointed by
/// the operator of this machine, for the one replica the passphrase-only
/// claim (peer-protocol 03 §6) cannot reach — one attributed before the
/// claim key existed, by a machine that died before any later offer could
/// publish one. Shared by the contract verb and the agent's
/// <c>reattribute</c> verb so the two refuse identically.
/// <para>
/// It also holds the operator's other decision about a replica stored here:
/// acknowledging a claim that moved one (FR-DR-005), shared the same way with
/// the agent's <c>acknowledge-claim</c> verb.
/// </para>
/// </summary>
internal static class ReplicaReattribution
{
    /// <summary>Every replica stored here, whose it is, and whether the passphrase can move it.</summary>
    /// <param name="owners">The attribution ledger.</param>
    /// <param name="grants">The pairings, for the owners' labels.</param>
    internal static ReplicaAttributionsResult List(ReplicaOwnerStore owners, PeerGrantStore grants) => new(
        [.. owners.All().Select(entry => new ReplicaAttributionDescriptor(
            entry.RepositoryIdHex,
            entry.Owner.Fingerprint,
            grants.Grants.FirstOrDefault(grant => string.Equals(
                grant.Identity.Fingerprint, entry.Owner.Fingerprint, StringComparison.Ordinal))?.Label,
            Claimable: entry.Owner.ClaimPublicKey is { Length: > 0 },
            ClaimAwaitingAcknowledgement: entry.Owner.ClaimAwaitingAcknowledgement))]);

    /// <summary>
    /// Re-points one replica, or says why not. The refusals are ordered so
    /// that the operator learns the cheapest fact first: the id's shape,
    /// then whether it is stored here, then whether the device is paired
    /// here, and only then whether this replica is the operator's to move.
    /// </summary>
    /// <param name="owners">The attribution ledger — the runtime's, which the listener serves from.</param>
    /// <param name="grants">The pairings the new owner must be among.</param>
    /// <param name="notices">Where the change is put on the record.</param>
    /// <param name="repositoryId">The replica's repository id, lower-hex.</param>
    /// <param name="fingerprintPrefix">The new owner's fingerprint, or an unambiguous prefix.</param>
    /// <param name="nowUnixMilliseconds">When.</param>
    /// <returns>A <see cref="ConfigurationChangeResult"/> saying what moved, or a <see cref="ServiceError"/>.</returns>
    internal static ServiceResult Apply(
        ReplicaOwnerStore owners,
        PeerGrantStore grants,
        NoticeStore notices,
        string? repositoryId,
        string? fingerprintPrefix,
        ulong nowUnixMilliseconds)
    {
        if (!IsRepositoryId(repositoryId))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "Pass the replica's repository id: the 32-character hex name of its directory under the state "
                + "directory's `replicas` — list_replica_attributions (or `pairings`) shows them.");
        }

        var id = repositoryId!.ToLowerInvariant();
        var owner = owners.Find(id);
        if (owner is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No replica of repository {id} is attributed here.");
        }

        if (string.IsNullOrWhiteSpace(fingerprintPrefix))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "Pass the fingerprint of the paired device the replica now belongs to.");
        }

        var (grant, matchCount) = PeerUnpairing.Resolve(grants, fingerprintPrefix);
        if (matchCount == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"No pairing matches '{fingerprintPrefix}' — pair the device here first, as one that stores here, "
                + "and then re-point the replica at it.");
        }

        if (grant is null)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{fingerprintPrefix}' matches {matchCount} pairings; give more of the fingerprint.");
        }

        if (grant.Role == PeerRole.StoresForUs)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"{grant.Label} ({grant.Identity.Fingerprint}) is paired as a destination this machine stores at, "
                + "not as a device that stores here — a replica can only belong to a peer that stores here.");
        }

        // The whole point of the override is the replica the passphrase
        // cannot reach. Where a claim key IS on record the owner has a
        // proof, and an operator who could bypass it would make the weakest
        // path stand in for the strongest — on every replica, not just the
        // orphaned one.
        if (owner.ClaimPublicKey is { Length: > 0 })
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Replica {id} carries its owner's claim key, so its owner can claim it with the passphrase "
                + "(peer-protocol 03 §6). The operator's override exists only for a replica recorded before "
                + "the key was published, and is refused for this one.");
        }

        if (string.Equals(owner.Fingerprint, grant.Identity.Fingerprint, StringComparison.Ordinal))
        {
            return new ConfigurationChangeResult(
                [$"Replica {id} already belongs to {grant.Label} ({grant.Identity.Fingerprint}); nothing changed."]);
        }

        var previous = owner.Fingerprint;
        if (!owners.Reattribute(id, grant.Identity.Fingerprint))
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No replica of repository {id} is attributed here.");
        }

        // On the record and never auto-resolved: an attribution moved by
        // hand is a fact the next person reading this machine should see,
        // whether or not it was the right call.
        notices.Raise(
            $"replica-reattributed:{id}",
            $"Replica {id} was re-pointed by the operator: it now belongs to {grant.Label} "
            + $"({grant.Identity.Fingerprint}) and was attributed to {previous}. The former owner's offers and "
            + "retrievals for it are refused from now on.",
            nowUnixMilliseconds);

        return new ConfigurationChangeResult(
        [
            $"Replica {id} now belongs to {grant.Label} ({grant.Identity.Fingerprint}); it was attributed to {previous}.",
            $"The next offer or retrieval from {previous} for this repository is refused as stored here for another peer.",
            "The recorded reclaim and claim keys, if any, are unchanged — the new owner\'s passphrase re-derives them.",
        ]);
    }

    /// <summary>
    /// Acknowledges the claim that moved one replica here, or says why there
    /// is nothing to acknowledge. The refusals come in the same order as the
    /// re-point's: the id's shape, then whether the replica is stored here.
    /// </summary>
    /// <param name="owners">The attribution ledger — the runtime's, which the listener serves from.</param>
    /// <param name="grants">The pairings, for the claimant's label.</param>
    /// <param name="notices">Where the claim's notice is resolved.</param>
    /// <param name="repositoryId">The replica's repository id, lower-hex.</param>
    /// <param name="nowUnixMilliseconds">When.</param>
    /// <returns>A <see cref="ConfigurationChangeResult"/> saying what changed, or a <see cref="ServiceError"/>.</returns>
    internal static ServiceResult AcknowledgeClaim(
        ReplicaOwnerStore owners,
        PeerGrantStore grants,
        NoticeStore notices,
        string? repositoryId,
        ulong nowUnixMilliseconds)
    {
        if (!IsRepositoryId(repositoryId))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "Pass the replica's repository id: the 32-character hex name of its directory under the state "
                + "directory's `replicas` — list_replica_attributions (or `pairings`) shows them.");
        }

        var id = repositoryId!.ToLowerInvariant();
        var owner = owners.Find(id);
        if (owner is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No replica of repository {id} is attributed here.");
        }

        var claimant = Describe(grants, owner.Fingerprint);
        if (!owners.AcknowledgeClaim(id))
        {
            return new ConfigurationChangeResult(
                [$"No claim on replica {id} awaits acknowledgement, so nothing changed; it belongs to {claimant}."]);
        }

        // The notice asked for exactly this, so the acknowledgement answers
        // it. Resolving keeps it on record rather than deleting it.
        notices.Resolve(ClaimNoticeKey(id), nowUnixMilliseconds);

        return new ConfigurationChangeResult(
        [
            $"Acknowledged the claim on replica {id}: {claimant} may now instruct retention for it, held to its "
            + "grant's retention floor like any owner.",
            "Reading it was never held. Nothing else changed.",
        ]);
    }

    /// <summary>
    /// Puts a claim that moved a replica in front of this machine's operator
    /// (peer-protocol 07 §5.9): who claimed it, whose it was, that reading is
    /// the claimant's already, and the acknowledgement that releases deleting.
    /// </summary>
    /// <remarks>
    /// Keyed to the replica, so a second claim before the first is
    /// acknowledged refreshes the one notice rather than stacking another,
    /// and the acknowledgement resolves it whichever claimant it names. The
    /// notice and the claim are acknowledged separately, because dismissing
    /// a notice says only that it was seen — the console's "acknowledge all"
    /// must not be a way to release a hold nobody looked at.
    /// </remarks>
    /// <param name="notices">The runtime's notices.</param>
    /// <param name="grants">The pairings, for the former owner's label.</param>
    /// <param name="claimant">The paired device the claim moved the replica to.</param>
    /// <param name="repositoryIdHex">The replica's repository id, lower-hex.</param>
    /// <param name="previousOwner">The fingerprint the replica was attributed to before the claim.</param>
    /// <param name="nowUnixMilliseconds">When.</param>
    internal static void RaiseClaimed(
        NoticeStore notices,
        PeerGrantStore grants,
        PeerGrant claimant,
        string repositoryIdHex,
        string previousOwner,
        ulong nowUnixMilliseconds) =>
        notices.Raise(
            ClaimNoticeKey(repositoryIdHex),
            $"'{claimant.Label}' ({claimant.Identity.Fingerprint}) proved it holds the owner's passphrase and "
            + $"claimed replica {repositoryIdHex}, which was stored here for {Describe(grants, previousOwner)}. "
            + "It can read the replica now; it cannot delete from it until you acknowledge the claim itself, from "
            + "the console or with the agent's `acknowledge-claim` verb. Acknowledging this notice only dismisses "
            + "it. If you were not expecting a claim, the passphrase may be in the wrong hands: leave the claim "
            + $"unacknowledged and end the pairing with '{claimant.Label}'.",
            nowUnixMilliseconds);

    /// <summary>The one notice a replica's claim raises and its acknowledgement resolves.</summary>
    private static string ClaimNoticeKey(string repositoryIdHex) => $"replica-claimed:{repositoryIdHex}";

    /// <summary>A peer's label and fingerprint, or the fingerprint alone when no pairing with it remains.</summary>
    private static string Describe(PeerGrantStore grants, string fingerprint) =>
        grants.Grants.FirstOrDefault(grant => string.Equals(
            grant.Identity.Fingerprint, fingerprint, StringComparison.Ordinal)) is { } grant
            ? $"{grant.Label} ({fingerprint})"
            : fingerprint;

    /// <summary>Thirty-two hex characters, either case.</summary>
    private static bool IsRepositoryId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
}
