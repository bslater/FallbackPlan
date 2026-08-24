using FallbackPlan.Domain;
using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Agent;

/// <summary>
/// The pairing-invite surface (ADR-0030 Amendment 3): issuing, listing and
/// revoking invites, and redeeming one against a remote service. Issuing is
/// this operator's approval; redeeming is the other's.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>How long an invite stays redeemable when the operator does not say.</summary>
    private const int DefaultInviteTimeToLiveMinutes = 24 * 60;

    /// <summary>The longest life an invite may be given — a code is a secret, not a standing credential.</summary>
    private const int MaximumInviteTimeToLiveMinutes = 30 * 24 * 60;

    private ServiceResult CreatePairingInvite(CreatePairingInviteCommand command)
    {
        if (!PeerRoles.TryParse(command.Role, out var role))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{command.Role}' is not a storage role (stores-here | stores-for-us | both).");
        }

        if (command.QuotaBytes is > 0 && role == PeerRole.StoresForUs)
        {
            // The same rule the pair verb enforces: a quota bounds what a peer
            // stores HERE, so it means nothing for a peer that only stores for
            // us — accepting it would record a ceiling nobody enforces.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "A quota bounds what the peer stores here; it does not apply to role 'stores-for-us'.");
        }

        var timeToLive = command.TimeToLiveMinutes ?? DefaultInviteTimeToLiveMinutes;
        if (timeToLive is < 1 or > MaximumInviteTimeToLiveMinutes)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"An invite lives between 1 minute and {MaximumInviteTimeToLiveMinutes} minutes; "
                + $"{timeToLive} is outside that.");
        }

        if (string.IsNullOrWhiteSpace(command.Label))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "An invite names the peer it will admit; pass a label.");
        }

        var invites = PairingInviteStore.Open(runtime.Options.StateDirectory);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var terms = new PeerTerms(command.QuotaBytes ?? 0, string.Empty, 0);

        try
        {
            var (code, invite) = invites.Issue(
                command.Label, role, terms, now, (ulong)timeToLive * 60_000UL);

            // The code exists in the clear exactly here, in this result. The
            // store keeps only the derived key, so there is no second showing.
            return new PairingInviteResult(
                code.Render(),
                invite.InviteIdHex,
                invite.ExpiresAt,
                remoteBinding.Enabled ? remoteBinding.Reason : null,
                remoteBinding.Enabled
                    ? null
                    : "The remote binding is off, so nobody can dial this invite yet — restart the "
                      + "service with --remote-interface and --remote-port, then give the peer that address.");
        }
        catch (Application.ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.Refused, exception.Message);
        }
    }

    private PairingInvitesResult ListPairingInvites() =>
        new([.. PairingInviteStore.Open(runtime.Options.StateDirectory).Invites
            .Select(invite => new PairingInviteDescriptor(
                invite.InviteIdHex, invite.Label, RoleName(invite.Role), invite.ExpiresAt, invite.ConsumedBy))]);

    private ServiceResult RevokePairingInvite(RevokePairingInviteCommand command) =>
        PairingInviteStore.Open(runtime.Options.StateDirectory).Revoke(command.InviteId)
            ? new AcknowledgedResult()
            : new ServiceError(
                ServiceErrorReason.NotFound, $"No invite '{command.InviteId}' is held here.");

    private async ValueTask<ServiceResult> PairWithInviteAsync(
        PairWithInviteCommand command, CancellationToken cancellationToken)
    {
        if (!PairingInviteCode.TryParse(command.Code, out var code, out var defect))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, defect!);
        }

        if (string.IsNullOrWhiteSpace(command.Host) || command.Port is < 1 or > 65535)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "Pass the remote service's host and port.");
        }

        using var keypair = PeerKeypairStore.Open(runtime.Options.StateDirectory);
        var grants = PeerGrantStore.Open(runtime.Options.StateDirectory);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                command.Host, command.Port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            var result = await PairingCeremony.OfferWithInviteAsync(
                connection.Stream, keypair, grants, Environment.MachineName, code!,
                initiatorBinding: connection.LocalBinding, responderBinding: connection.RemoteBinding,
                now, cancellationToken).ConfigureAwait(false);

            if (result.Grant is not { } grant)
            {
                var refusal = result.Refusal;
                return new ServiceError(
                    refusal?.Reason == PeerRefusalReason.InviteUnknown
                        ? ServiceErrorReason.Refused
                        : ServiceErrorReason.Failed,
                    refusal is null
                        ? "The pairing did not complete."
                        : $"The remote service refused the pairing: {refusal.Text}");
            }

            // The command's label is what THIS operator calls the peer; the
            // wire carried the peer's self-introduction, which the operator
            // never chose. Relabel, so the destination list speaks their words.
            var label = string.IsNullOrWhiteSpace(command.Label) ? grant.Label : command.Label;
            if (!string.Equals(label, grant.Label, StringComparison.Ordinal))
            {
                grants.Relabel(grant.Identity, label);
            }

            return new PairingCompletedResult(
                grant.Identity.Fingerprint,
                label,
                RoleName(PeerRoles.Complement(grant.Role)),
                grant.Role is PeerRole.StoresForUs or PeerRole.Both ? grant.Terms.QuotaBytes : null);
        }
        catch (Exception exception) when (exception is SocketException or IOException
            or System.Security.Authentication.AuthenticationException)
        {
            return new ServiceError(
                ServiceErrorReason.Unavailable,
                $"Could not reach {command.Host}:{command.Port} — {exception.Message}");
        }
        catch (PeerProtocolException exception)
        {
            return new ServiceError(
                ServiceErrorReason.Failed, $"The pairing failed: {exception.Message}");
        }
    }

    /// <summary>
    /// A best-effort termination dial is bounded so a black-holed peer cannot
    /// hang the caller — revocation never waits on the announcement anyway.
    /// </summary>
    private static readonly TimeSpan UnpairNotifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ends a pairing over the contract (ADR-0039): the same mechanics as the
    /// agent's unpair verb — resolve, announce best-effort, revoke, tombstone
    /// — shared through <see cref="PeerUnpairing"/> so the two cannot drift.
    /// </summary>
    private async ValueTask<ServiceResult> UnpairAsync(UnpairCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Fingerprint))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "Pass the pairing's fingerprint.");
        }

        var grants = PeerGrantStore.Open(runtime.Options.StateDirectory);
        var (grant, matchCount) = PeerUnpairing.Resolve(grants, command.Fingerprint);
        if (matchCount == 0)
        {
            return new ServiceError(ServiceErrorReason.NotFound, $"No pairing matches '{command.Fingerprint}'.");
        }

        if (grant is null)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{command.Fingerprint}' matches {matchCount} pairings; give more of the fingerprint.");
        }

        // A revocation must not silently break what sets sync to: while a
        // configured destination references this peer, the grant stays and
        // the refusal names the destination — the honest order is delete the
        // destination first (ADR-0037 §4's no-cascade posture, pointed both
        // ways: delete_destination's peer refusal names unpair, and unpair's
        // names the destination). An unloadable configuration skips the
        // check — an invalid file must not block a revocation.
        List<string> referencing;
        try
        {
            referencing = [.. runtime.Configuration.Destinations
                .Where(destination => destination.Kind == DestinationKind.Peer
                    && string.Equals(destination.Fingerprint, grant.Identity.Fingerprint, StringComparison.Ordinal))
                .Select(destination => destination.Name)];
        }
        catch (ClientStateException)
        {
            referencing = [];
        }

        if (referencing.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Destination '{string.Join("', '", referencing)}' still points at this peer — delete the "
                + "destination first, or sets that reference it would sync into a revoked grant.");
        }

        var lines = new List<string>();
        if (command.Notify)
        {
            var endpoint = command.Endpoint
                ?? PeerUnpairing.EndpointFor(runtime.Options.StateDirectory, grant.Identity.Fingerprint);
            if (endpoint is null)
            {
                lines.Add("No endpoint is known for the peer — it will learn of the ending at its next dial.");
            }
            else
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(UnpairNotifyTimeout);
                try
                {
                    lines.Add(await PeerUnpairing.TryNotifyTerminationAsync(
                        runtime.Options.StateDirectory, grants, grant, endpoint, bounded.Token)
                        .ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lines.Add("The peer did not answer in time — it will learn of the ending at its next dial.");
                }
            }
        }

        grants.Revoke(grant.Identity);
        lines.Add(
            $"Revoked the pairing with {grant.Label} ({grant.Identity.Fingerprint}). A tombstone remains, so "
            + "its next dial is told 'revoked' rather than 'never paired'.");
        lines.Add("Objects already stored at the peer are theirs to keep or evict — revocation deletes nothing anywhere.");
        return new ConfigurationChangeResult(lines);
    }

    /// <summary>
    /// Claims the replicas peers hold for this household (ADR-0046;
    /// peer-protocol 07 §5) — the verb a machine rebuilt from bare metal uses
    /// to reach data whose attribution names a device identity that no longer
    /// exists.
    /// </summary>
    /// <remarks>
    /// Every peer is asked in turn and every answer is reported, including the
    /// ones that could not be reached. A claim is run in the hours after a
    /// household lost a machine, which is exactly when the far end is least
    /// likely to be awake; one silent friend must not cost the others their
    /// turn, and "could not ask" must not read as "holds nothing".
    /// </remarks>
    private async ValueTask<ServiceResult> ClaimReplicasAsync(
        ClaimReplicasCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Envelope))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "Pass the sealed claim envelope — the passphrase's root, sealed to this service's recipient key.");
        }

        var grants = PeerGrantStore.Open(runtime.Options.StateDirectory);
        var targets = new List<PeerGrant>();

        if (command.Fingerprint is { Length: > 0 } fingerprint)
        {
            var (grant, matchCount) = PeerUnpairing.Resolve(grants, fingerprint);
            if (matchCount == 0)
            {
                return new ServiceError(ServiceErrorReason.NotFound, $"No pairing matches '{fingerprint}'.");
            }

            if (grant is null)
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{fingerprint}' matches {matchCount} pairings; give more of the fingerprint.");
            }

            targets.Add(grant);
        }
        else
        {
            targets.AddRange(grants.Grants);
        }

        if (targets.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                "This device has no pairings. Pair with the peer holding your backups first — a claim proves "
                + "who you are to somebody who already agreed to talk to you.");
        }

        byte[] claimRoot;
        try
        {
            claimRoot = runtime.GrantRecipient.OpenClaimRoot(Convert.FromHexString(command.Envelope));
        }
        catch (Exception exception) when (exception is FormatException or SealedContentException)
        {
            // The two ways this fails read the same to a person and lead to
            // the same fix, so they are worded as one: the envelope was not
            // sealed to this service, or was not a claim root at all.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The claim envelope did not open. Seal it to this service's recipient key — describe_service "
                + "publishes it — and check the passphrase it was derived from.");
        }

        try
        {
            var log = runtime.LoggerFor(typeof(ClaimInitiator));
            var peers = new List<ClaimedFromPeer>(targets.Count);
            var moved = 0;

            foreach (var grant in targets)
            {
                var endpoint = command.Endpoint
                    ?? PeerUnpairing.EndpointFor(runtime.Options.StateDirectory, grant.Identity.Fingerprint);

                var outcome = await ClaimInitiator.ClaimAsync(
                    runtime.Options.StateDirectory, grants, grant, endpoint, claimRoot, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Unreachable is { } reason)
                {
                    Log.ClaimPeerUnreachable(log, grant.Identity.Fingerprint, reason);
                }
                else if (outcome.Claimed.Count > 0)
                {
                    Log.ReplicasClaimedFrom(log, grant.Identity.Fingerprint, outcome.Claimed.Count);
                }

                peers.Add(new ClaimedFromPeer(
                    grant.Identity.Fingerprint,
                    [.. outcome.Claimed.Select(Describe)],
                    outcome.Unreachable));
                moved += outcome.Claimed.Count;
            }

            if (moved > 0)
            {
                // Durable, because the next step is the operator's: the set
                // ids that came back are the one piece of lost configuration
                // nothing else can supply (07 §5.8), and they are worth
                // nothing if the window they appeared in is closed.
                runtime.Notices.Raise(
                    "replicas-claimed",
                    $"Claimed {moved} replica(s) from {targets.Count} peer(s) by proving the passphrase. "
                    + "Restores can run against them now; the sets they name still have to be rebuilt here.",
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            return new ClaimedReplicasResult(peers);
        }
        finally
        {
            // Held for the exchange and no longer. Every seed derived from it
            // was zeroed as it was used; this is the last copy.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(claimRoot);
        }
    }

    /// <summary>The wire's answer as the contract's, both halves rendered hex.</summary>
    private static ClaimedReplicaDescriptor Describe(ClaimedReplica replica) =>
        new(Convert.ToHexStringLower(replica.RepositoryId.Span),
            [.. replica.BackupSetIds.Select(id => Convert.ToHexStringLower(id.Span))]);
}
