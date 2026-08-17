using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Protocol;
using ProtocolIdentity = FallbackPlan.Protocol.PeerIdentity;

namespace FallbackPlan.Cli;

/// <summary>
/// A live remote-binding connection to a paired service, owning the client and
/// the device keypair it authenticated with (ADR-0028 §5; ADR-0030).
/// </summary>
/// <remarks>
/// <see cref="RemoteServiceClient"/> holds the keypair for the life of the
/// connection — a watch re-dials and re-signs with it — but does not own its
/// disposal. This holder does: disposing it closes the client's connection and
/// then zeroes the key material, in that order.
/// </remarks>
internal sealed class RemoteConnection(IFallbackPlanClient client, PeerKeypair keypair) : IAsyncDisposable
{
    /// <summary>The command client over the opened session.</summary>
    public IFallbackPlanClient Client => client;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync().ConfigureAwait(false);
        keypair.Dispose();
    }
}

/// <summary>
/// Resolves and opens a connection to a service this console has paired with
/// (ADR-0030). The endpoint is not enough to know who should answer: a grant
/// records the peer's key, its label, its role and its terms, but never an
/// address (a deliberate invariant — the key is the identity, and an address
/// carries no authority). So the caller names the pinned service by its
/// fingerprint, and the full key behind that fingerprint is what the handshake
/// is held to.
/// </summary>
internal static class RemotePeer
{
    /// <summary>
    /// Opens an authenticated session to <paramref name="host"/>:<paramref name="port"/>,
    /// expecting the pinned service whose fingerprint is <paramref name="fingerprint"/>.
    /// </summary>
    /// <param name="host">The service's host.</param>
    /// <param name="port">The service's remote-binding port.</param>
    /// <param name="stateDirectory">The console's state directory (its peer identity and pairings).</param>
    /// <param name="fingerprint">The fingerprint of the pinned service to expect.</param>
    /// <param name="clientName">What to call this client in the service's log.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The open connection; dispose it to close the session and release the key.</returns>
    /// <exception cref="CliFailureException">
    /// No pairing matches the fingerprint, or the service could not be reached or refused.
    /// </exception>
    public static async ValueTask<RemoteConnection> ConnectAsync(
        string host,
        int port,
        string stateDirectory,
        string fingerprint,
        string clientName,
        CancellationToken cancellationToken)
    {
        var keypair = PeerKeypairStore.Open(stateDirectory);
        try
        {
            var grants = PeerGrantStore.Open(stateDirectory);
            var expected = Resolve(grants, fingerprint);

            try
            {
                var client = await RemoteServiceClient.ConnectAsync(
                    host, port, keypair, grants, expected, clientName, cancellationToken).ConfigureAwait(false);

                return new RemoteConnection(client, keypair);
            }
            catch (Exception exception) when (exception is ServiceConnectionException or IOException or SocketException)
            {
                // A remote console has no direct-mode fallback the way the local
                // gateway does, so an unreachable or refusing service is a failure
                // to report, not a signal to do the work here. A refusal reaches
                // us either as a stated ServiceConnectionException or — when the
                // service closes first — as a dropped connection; both mean the
                // same thing to the operator.
                throw new CliFailureException(
                    $"could not reach the paired service at {host}:{port}: {exception.Message}", exception);
            }
        }
        catch
        {
            keypair.Dispose();
            throw;
        }
    }

    /// <summary>Finds the pinned identity for a fingerprint, or refuses.</summary>
    private static ProtocolIdentity Resolve(PeerGrantStore grants, string fingerprint)
    {
        // A fingerprint is derived from the key, so at most one grant can carry
        // it; the full key it names is what is handed to the handshake.
        foreach (var grant in grants.Grants)
        {
            if (string.Equals(grant.Identity.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return grant.Identity;
            }
        }

        throw new CliFailureException($"no pairing matches fingerprint '{fingerprint}'.");
    }
}
