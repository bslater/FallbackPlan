using FallbackPlan.Domain;
using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The mechanics of ending a pairing (ADR-0030 Amendment 2), shared by the
/// agent's <c>unpair</c> verb and the contract's <c>unpair</c> command so the
/// two cannot drift: resolve the fingerprint to exactly one grant, announce
/// the ending best-effort while the grant still authenticates the session,
/// and leave revocation itself — which never waits on the announcement — to
/// the caller.
/// </summary>
internal static class PeerUnpairing
{
    /// <summary>
    /// Resolves a fingerprint, or an unambiguous prefix of one, to exactly one
    /// grant. A fingerprint is a display handle, never the identity — so an
    /// ambiguous prefix is refused rather than guessed, and revocation always
    /// acts on the full key (ADR-0030 §1).
    /// </summary>
    /// <param name="grants">The grant store.</param>
    /// <param name="fingerprintPrefix">The fingerprint or prefix, case-insensitive.</param>
    /// <returns>The single match, and how many matched (0, 1, or more).</returns>
    internal static (PeerGrant? Grant, int Matches) Resolve(PeerGrantStore grants, string fingerprintPrefix)
    {
        var matches = grants.Grants
            .Where(grant => grant.Identity.Fingerprint.StartsWith(fingerprintPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (matches.Count == 1 ? matches[0] : null, matches.Count);
    }

    /// <summary>The configured endpoint for a peer destination, when the address book has one.</summary>
    /// <param name="stateDirectory">The state directory holding <c>config.json</c>.</param>
    /// <param name="peerFingerprint">The peer's full fingerprint.</param>
    /// <returns>The endpoint, or null when none is configured or the configuration will not load.</returns>
    internal static string? EndpointFor(string stateDirectory, string peerFingerprint)
    {
        try
        {
            return ClientConfiguration.Load(Path.Combine(stateDirectory, "config.json")).Destinations
                .FirstOrDefault(destination =>
                    destination.Kind == DestinationKind.Peer
                    && string.Equals(destination.Fingerprint, peerFingerprint, StringComparison.Ordinal))
                ?.Endpoint;
        }
        catch (ClientStateException)
        {
            // An invalid configuration must not block a revocation.
            return null;
        }
    }

    /// <summary>
    /// Best-effort termination notice, dialled while the grant still
    /// authenticates the session (ADR-0030 Amendment 2): the ending should be
    /// a stated fact at the other house, not unexplained refusals. Never
    /// throws for a peer that cannot be told — the answer says what happened,
    /// and an unreachable peer learns from the Revoked refusal at its next
    /// dial instead.
    /// </summary>
    /// <param name="stateDirectory">The state directory holding the device key.</param>
    /// <param name="grants">The grant store the session authenticates against.</param>
    /// <param name="grant">The pairing being ended.</param>
    /// <param name="endpoint">Where to dial, as <c>host:port</c>.</param>
    /// <param name="cancellationToken">Bounds the dial.</param>
    /// <returns>One report line describing the outcome.</returns>
    internal static async Task<string> TryNotifyTerminationAsync(
        string stateDirectory,
        PeerGrantStore grants,
        PeerGrant grant,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
        {
            return $"'{endpoint}' is not host:port — the peer will learn of the ending at its next dial.";
        }

        try
        {
            using var keypair = PeerKeypairStore.Open(stateDirectory);
            await using var connection = await PeerTlsConnection.DialAsync(
                host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            // A termination notice demands nothing of the peer beyond hearing
            // it: this dial carries no feature requirement.
            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!session.Supports(PeerSessionNegotiation.TerminationNoticeFeature))
            {
                // An older peer is simply not sent a type it cannot parse
                // (02: unknown types hard-fail); it learns from the refusal.
                return "the peer predates termination notices — it will learn at its next dial.";
            }

            await PeerFrame.WriteAsync(
                session.Stream,
                new PeeringTermination("the operator ended the pairing", GraceDays: 30),
                cancellationToken).ConfigureAwait(false);
            return $"notified {grant.Label} that the peering has ended.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"could not notify the peer ({exception.Message}) — it will learn of the ending at its next dial.";
        }
    }

    /// <summary>Parses <c>host:port</c>; false for anything else.</summary>
    /// <param name="target">The text to parse.</param>
    /// <param name="host">The host half.</param>
    /// <param name="port">The port half, 1–65535.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryParseEndpoint(string target, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || colon == target.Length - 1)
        {
            return false;
        }

        host = target[..colon];
        return int.TryParse(target[(colon + 1)..], CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }
}
