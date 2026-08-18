using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Protocol;

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
}
