using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using System.Security.Cryptography;

namespace FallbackPlan.Agent;

/// <summary>
/// The claimant side of the replica claim (peer-protocol 07 §5; ADR-0046):
/// dial a peer that stores replicas for this household, and prove with the
/// <em>passphrase</em> that the household is the same one, whatever device
/// identity it wears now.
/// </summary>
/// <remarks>
/// <para>
/// This runs on a machine that has lost everything durable. Its device key is
/// gone by design — a recovery kit that could reproduce one would be a worse
/// credential than the key it replaced — so the destination's attribution
/// names an identity that no longer exists. What survived is the passphrase,
/// and the credential it derives is the only thing that can move that
/// attribution.
/// </para>
/// <para>
/// The private half is derived here, once per candidate, from the token the
/// destination just sent. It never leaves this process, and the root it comes
/// from is zeroed by the caller as soon as the exchange ends.
/// </para>
/// </remarks>
internal static class ClaimInitiator
{
    /// <summary>What one peer answered.</summary>
    /// <param name="Claimed">The replicas whose attribution moved here.</param>
    /// <param name="Unreachable">
    /// Why the peer could not be asked, or null when it answered. Never an
    /// exception: a claim asks several households at once, and the one that
    /// did not answer must not cost the others their turn.
    /// </param>
    public sealed record Outcome(IReadOnlyList<ClaimedReplica> Claimed, string? Unreachable = null);

    /// <summary>Claims whatever one peer will yield to this passphrase.</summary>
    /// <param name="stateDirectory">Where this device's peer keypair lives.</param>
    /// <param name="grants">The pinned pairings, for authenticating the session.</param>
    /// <param name="grant">The pairing to dial.</param>
    /// <param name="endpoint">Where to dial, as <c>host:port</c>.</param>
    /// <param name="claimRoot">The 32-byte Argon2id root the passphrase produced.</param>
    /// <param name="cancellationToken">Bounds the exchange.</param>
    /// <returns>What moved, or why nothing could be asked.</returns>
    public static async Task<Outcome> ClaimAsync(
        string stateDirectory,
        PeerGrantStore grants,
        PeerGrant grant,
        string? endpoint,
        ReadOnlyMemory<byte> claimRoot,
        CancellationToken cancellationToken)
    {
        if (endpoint is null || !PeerUnpairing.TryParseEndpoint(endpoint, out var host, out var port))
        {
            return new Outcome([], endpoint is null
                ? "no endpoint is recorded for this pairing — say where to dial."
                : $"'{endpoint}' is not host:port.");
        }

        try
        {
            using var keypair = PeerKeypairStore.Open(stateDirectory);
            await using var connection = await PeerTlsConnection.DialAsync(
                host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!session.Supports(PeerSessionNegotiation.ReplicaClaimFeature))
            {
                // Not sent a frame it cannot parse: 02 hard-fails on an
                // unknown type, so an older peer is told nothing and the
                // operator is told why.
                return new Outcome([], "the peer predates replica claims — it cannot serve one yet.");
            }

            await PeerFrame.WriteAsync(session.Stream, new ClaimRequest(), cancellationToken)
                .ConfigureAwait(false);

            var challenge = await ReplicationWire.ReadAsync(
                session.Stream, PeerMessageType.ClaimChallenge, ClaimChallenge.Read, cancellationToken)
                .ConfigureAwait(false);

            if (challenge.Candidates.Count == 0)
            {
                // The ordinary answer for a peer with nothing waiting, and a
                // statement rather than a fault (07 §5.5).
                return new Outcome([]);
            }

            var answers = BuildAnswers(
                challenge, claimRoot.Span, session.TranscriptHash.Span, keypair.Identity.PublicKey);

            await PeerFrame.WriteAsync(session.Stream, new ClaimProof(answers), cancellationToken)
                .ConfigureAwait(false);

            var result = await ReplicationWire.ReadAsync(
                session.Stream, PeerMessageType.ClaimResult, ClaimResult.Read, cancellationToken)
                .ConfigureAwait(false);

            return new Outcome(result.Claimed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Outcome([], exception.Message);
        }
    }

    /// <summary>
    /// One signed answer per candidate. The message is rebuilt from the
    /// candidate's own fields and this session's transcript, so a proof
    /// captured at one destination is inert at another — the token differs, so
    /// the keypair differs, and the transcript differs besides.
    /// </summary>
    private static List<ClaimAnswer> BuildAnswers(
        ClaimChallenge challenge,
        ReadOnlySpan<byte> claimRoot,
        ReadOnlySpan<byte> transcriptHash,
        ReadOnlySpan<byte> claimantIdentity)
    {
        var answers = new List<ClaimAnswer>(challenge.Candidates.Count);
        foreach (var candidate in challenge.Candidates)
        {
            var seed = ClaimKeyDeriver.DeriveSeed(claimRoot, candidate.ClaimToken.Span);
            try
            {
                // The public half through the same function that registered
                // it (ClaimArming), so the two cannot drift apart: the
                // destination compares byte for byte against what a source
                // sent it while the pairing was still alive.
                var publicKey = ClaimKeyDeriver.PublicKeyOf(seed);
                var message = ReplicaClaimProof.Message(
                    candidate.RepositoryId.Span,
                    candidate.ClaimToken.Span,
                    candidate.Nonce.Span,
                    transcriptHash,
                    claimantIdentity);

                answers.Add(new ClaimAnswer(
                    candidate.RepositoryId, publicKey, ReplicaClaimProof.Sign(seed, message)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }

        return answers;
    }
}
