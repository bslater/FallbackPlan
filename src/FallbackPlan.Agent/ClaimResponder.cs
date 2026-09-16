using Bodu;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination side of the claim ceremony (peer-protocol 03 §6;
/// [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)):
/// a machine rebuilt after total loss proves a replica is its own, and the
/// attribution follows it to the device identity it now has.
/// </summary>
/// <remarks>
/// <para>
/// What makes this safe is that the proof is a key the <em>previous</em>
/// machine published, at first attribution, while it still existed — and one
/// this destination has never been able to change since. A claimant that can
/// sign under it holds the installation's passphrase and kit, which is the
/// same person who could open every backup in the replica anyway.
/// </para>
/// <para>
/// A claim that matches nothing and a claim whose signature does not verify
/// refuse <b>identically</b>, which is the same rule
/// <see cref="RetrievalResponder"/> follows for the same reason: telling the
/// two apart would turn this into a way to ask a stranger's peer whether it
/// holds a given key. Both answers are computed before either is acted on, so
/// the refusal does not time differently either.
/// </para>
/// <para>
/// Nothing here decrypts, and nothing here is a restore. The claim moves a
/// pointer in the attribution ledger; what the claimant may then read back it
/// reads through the ordinary retrieval session, and can only open with the
/// passphrase it already proved it has.
/// </para>
/// </remarks>
internal static class ClaimResponder
{
    /// <summary>Serves one claim over an open peer stream.</summary>
    /// <param name="stream">The open session stream, positioned after the claim.</param>
    /// <param name="peer">The authenticated claimant.</param>
    /// <param name="owners">The replica attribution ledger (peer-protocol 05 §2).</param>
    /// <param name="sessionId">This session's identifier (02 §3.5).</param>
    /// <param name="claim">The already-read claim.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The repositories re-attributed.</returns>
    public static async Task<IReadOnlyList<string>> ServeAsync(
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        ReadOnlyMemory<byte> sessionId,
        ReplicationClaim claim,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);
        ThrowHelper.ThrowIfNull(claim);

        try
        {
            // A session that never authenticated has no identifier, and a
            // claim signed over nothing would verify in every session at
            // once. Refused before the signature is looked at.
            if (sessionId.Length != SessionBinding.SessionIdLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    "A claim is bound to the session it is made in, and this session has no identifier (03 §6).");
            }

            var presented = Convert.ToHexStringLower(claim.ClaimPublicKey.Span);
            var verified = Repository.Crypto.RepositorySigner.VerifyWithPublicKey(
                claim.ClaimPublicKey.Span,
                ReplicationClaim.EncodeForSigning(sessionId.Span, peer.Identity.Fingerprint),
                claim.Signature.Span);
            var claimed = owners.ClaimedBy(presented);

            if (!verified || claimed.Count == 0)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    "No replica here is claimable under that key.");
            }

            foreach (var repositoryIdHex in claimed)
            {
                owners.Reattribute(repositoryIdHex, peer.Identity.Fingerprint);
            }

            await PeerFrame.WriteAsync(
                stream,
                new ReplicationClaimAccepted([.. claimed.Select(id => (ReadOnlyMemory<byte>)Convert.FromHexString(id))]),
                cancellationToken)
                .ConfigureAwait(false);

            return claimed;
        }
        catch (PeerProtocolException exception)
        {
            if (!exception.ReceivedFromPeer)
            {
                await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
            }

            throw;
        }
    }
}
