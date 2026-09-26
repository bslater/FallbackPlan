using Bodu;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination side of the claim ceremony (peer-protocol 03 §6;
/// [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// Amendment 2): a machine rebuilt after total loss proves a replica is its
/// own with the passphrase and nothing else, and the attribution follows it
/// to the device identity it now has.
/// </summary>
/// <remarks>
/// <para>
/// What makes this safe is that the proof is a key the <em>previous</em>
/// machine published, at first attribution, while it still existed — and one
/// this destination has never been able to change since. A claimant that can
/// sign under it holds the installation's passphrase, which is the same
/// person who could open every backup in the replica anyway.
/// </para>
/// <para>
/// The claimant cannot derive that key alone: it derives from the passphrase
/// and the installation's KDF salt, and the salt is inside the replica,
/// behind the attribution gate. So the ceremony has two phases in one
/// session. The claimant opens; this side answers with the distinct
/// salt-and-parameter pairs behind every replica here that carries a claim
/// key — to a paired peer, and only what a paired peer could learn by holding
/// a replica; never a repository id, never a sealing public key, never whose
/// each pair is — and the claimant answers with one signed claim per pair.
/// </para>
/// <para>
/// A claim that matches nothing and a claim whose signature does not verify
/// refuse <b>identically</b>, which is the same rule
/// <see cref="RetrievalResponder"/> follows for the same reason: telling the
/// two apart would turn this into a way to ask a stranger's peer whether it
/// holds a given key. Both answers are computed for every entry before any
/// is acted on, so the refusal does not time differently either. A wrong
/// passphrase reaches here as a key nobody recorded and is refused the same
/// way — the claimant can only be told "wrong passphrase" by an archive of
/// its own, and it has none.
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
    /// <summary>Serves one claim ceremony over an open peer stream.</summary>
    /// <param name="replicasRoot">Where this peer keeps the replicas it stores.</param>
    /// <param name="stream">The open session stream, positioned after the claimant's open.</param>
    /// <param name="peer">The authenticated claimant.</param>
    /// <param name="owners">The replica attribution ledger (peer-protocol 05 §2).</param>
    /// <param name="sessionId">This session's identifier (02 §3.5).</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The repositories re-attributed.</returns>
    public static async Task<IReadOnlyList<string>> ServeAsync(
        string replicasRoot,
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        ReadOnlyMemory<byte> sessionId,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);

        try
        {
            // A session that never authenticated has no identifier, and a
            // claim signed over nothing would verify in every session at
            // once. Refused before anything is served.
            if (sessionId.Length != SessionBinding.SessionIdLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    "A claim is bound to the session it is made in, and this session has no identifier (03 §6).");
            }

            var parameters = await ParametersAsync(replicasRoot, owners, cancellationToken).ConfigureAwait(false);
            await PeerFrame.WriteAsync(stream, parameters, cancellationToken).ConfigureAwait(false);

            // Exactly one claim follows, and nothing else may: the answer
            // above is the whole of what a claimant needs, and a second
            // round would be a second look at the ledger.
            var claim = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationClaim, ReplicationClaim.Read, cancellationToken)
                .ConfigureAwait(false);

            var signed = ReplicationClaim.EncodeForSigning(sessionId.Span, peer.Identity.Fingerprint);
            var verified = true;
            var claimed = new SortedSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < claim.ClaimPublicKeys.Count; index++)
            {
                // Every entry is checked and every match collected before
                // either decides anything — an early exit on the first bad
                // signature would time differently from a claim that matched
                // nothing.
                verified &= Repository.Crypto.RepositorySigner.VerifyWithPublicKey(
                    claim.ClaimPublicKeys[index].Span, signed, claim.Signatures[index].Span);
                foreach (var repositoryIdHex in owners.ClaimedBy(Convert.ToHexStringLower(claim.ClaimPublicKeys[index].Span)))
                {
                    claimed.Add(repositoryIdHex);
                }
            }

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

            var accepted = claimed.ToList();
            await PeerFrame.WriteAsync(
                stream,
                new ReplicationClaimAccepted([.. accepted.Select(id => (ReadOnlyMemory<byte>)Convert.FromHexString(id))]),
                cancellationToken)
                .ConfigureAwait(false);

            return accepted;
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

    /// <summary>
    /// The distinct salt-and-parameter pairs behind every claimable replica
    /// here, sorted by salt (03 §6). A replica whose descriptor does not read
    /// is skipped rather than fatal: the claimant should still reach the
    /// replicas that do, and the damaged one is a finding for the sweep.
    /// </summary>
    private static async Task<ReplicationClaimParameters> ParametersAsync(
        string replicasRoot, Application.ReplicaOwnerStore owners, CancellationToken cancellationToken)
    {
        var pairs = new SortedDictionary<byte[], Argon2Parameters>(Comparer<byte[]>.Create(
            (left, right) => left.AsSpan().SequenceCompareTo(right)));

        foreach (var (repositoryIdHex, _) in owners.WithClaimKey())
        {
            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            if (!Directory.Exists(replicaPath))
            {
                continue;
            }

            try
            {
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                    new LocalFileSystemObjectStore(replicaPath), cancellationToken).ConfigureAwait(false);
                var salt = descriptor.KdfSalt.ToArray();
                if (salt.Length == ReplicationClaimParameters.SaltLength && !pairs.ContainsKey(salt))
                {
                    pairs.Add(salt, descriptor.KdfParameters);
                }
            }
            catch (Exception exception) when (exception is RepositoryOpenException or IOException or UnauthorizedAccessException)
            {
                // A replica with no readable descriptor cannot be claimed
                // through this ceremony; it is not a reason to refuse the
                // ones that can.
            }
        }

        // The wire bound is the wire bound: a destination storing for more
        // installations than the answer can name serves the first by salt
        // order, which is the one order that says nothing about who arrived
        // when. A household is one or two installations.
        var served = pairs.Take(ReplicationClaimParameters.MaximumEntries).ToList();

        return new ReplicationClaimParameters(
            [.. served.Select(pair => (ReadOnlyMemory<byte>)pair.Key)],
            [.. served.Select(pair => pair.Value.MemoryKiB)],
            [.. served.Select(pair => pair.Value.Iterations)],
            [.. served.Select(pair => pair.Value.Parallelism)]);
    }
}
