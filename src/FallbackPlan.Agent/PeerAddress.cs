using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Turns a peer destination's declaration into the two things needed to reach
/// it: the grant holding its key, and the host and port to dial.
/// </summary>
/// <remarks>
/// The address book and the trust decision live in different files on purpose
/// (FR-DEST-006, ADR-0030) — the configuration names where, the grant names
/// who — so reaching a peer always means joining the two. This is where they
/// are joined, once, so the fan-out and the admission probe cannot disagree
/// about what "declared but unreachable" means or word it differently.
/// </remarks>
internal static class PeerAddress
{
    /// <summary>A peer destination's key and dialling address.</summary>
    /// <param name="Grants">The grant store, which the session driver also needs.</param>
    /// <param name="Grant">The grant pinning this peer's identity.</param>
    /// <param name="Host">The host to dial.</param>
    /// <param name="Port">The port to dial.</param>
    internal sealed record Resolved(
        Protocol.PeerGrantStore Grants, Protocol.PeerGrant Grant, string Host, int Port);

    /// <summary>
    /// Resolves the destination, or says in one sentence why it cannot be
    /// reached as declared.
    /// </summary>
    /// <param name="runtime">The service, for the state directory holding the grants.</param>
    /// <param name="destination">The peer destination's declaration.</param>
    /// <param name="resolved">The grant and address, when there is one.</param>
    /// <param name="refusal">Why not, otherwise.</param>
    internal static bool TryResolve(
        ServiceRuntime runtime,
        DestinationConfiguration destination,
        out Resolved? resolved,
        out string? refusal)
    {
        resolved = null;

        var grants = Protocol.PeerGrantStore.Open(runtime.Options.StateDirectory);
        var grant = grants.Grants.FirstOrDefault(candidate =>
            string.Equals(candidate.Identity.Fingerprint, destination.Fingerprint, StringComparison.Ordinal));
        if (grant is null)
        {
            refusal = $"no pairing matches fingerprint '{destination.Fingerprint}' — pair with the peer first";
            return false;
        }

        var endpoint = destination.Endpoint!;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out var port))
        {
            refusal = $"endpoint '{endpoint}' is not host:port";
            return false;
        }

        refusal = null;
        resolved = new Resolved(grants, grant, endpoint[..separator], port);
        return true;
    }
}
