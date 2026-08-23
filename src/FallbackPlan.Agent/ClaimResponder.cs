using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using System.Security.Cryptography;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination side of the replica claim (peer-protocol 07 §5;
/// ADR-0046): a peer that has lost its device identity proves it holds the
/// repository's <em>passphrase</em>, and the attribution moves to whatever
/// identity it has now.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs on a destination, which holds no repository key of any
/// kind. That is why it can work: the credential it compares against is a
/// public key a source registered while the pairing was still alive, and the
/// signature is checked with nothing else. A destination never learns the
/// passphrase, never derives anything from it, and cannot itself produce a
/// proof for a replica it stores.
/// </para>
/// <para>
/// The set identifiers the result carries come from the store's own key
/// namespace — <c>snapshots/&lt;device-id&gt;/&lt;backup-set-id&gt;/…</c>
/// (specification 01 §2), where the set id is opaque rather than secret — so
/// answering them needs no decryption either.
/// </para>
/// </remarks>
internal static class ClaimResponder
{
    /// <summary>A backup-set identifier is 16 bytes.</summary>
    private const int SetIdLength = 16;

    /// <summary>Serves one claim exchange over an open peer stream.</summary>
    /// <param name="replicasRoot">Where replicas live, one directory per repository id.</param>
    /// <param name="stream">The open session stream, positioned after the claim request.</param>
    /// <param name="peer">
    /// The authenticated peer. Its 32-byte public key — not the displayed
    /// fingerprint, which is a truncated hash — is what a proof binds to.
    /// </param>
    /// <param name="owners">The attribution ledger (peer-protocol 05 §2).</param>
    /// <param name="transcriptHash">This session's bound context hash (02 §3.2).</param>
    /// <param name="stateDirectory">Where a durable notice is raised when an attribution moves.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The repository ids whose attribution moved, for the caller to log.</returns>
    public static async Task<IReadOnlyList<string>> ServeAsync(
        string replicasRoot,
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        ReadOnlyMemory<byte> transcriptHash,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        var fingerprint = peer.Identity.Fingerprint;

        // One challenge per replica this identity does not already own that
        // carries a registered credential. A replica with no credential is not
        // offered at all: it cannot be claimed, and pretending otherwise would
        // send a claimant hunting for a passphrase problem it does not have.
        var issued = new Dictionary<string, ClaimCandidate>(StringComparer.Ordinal);
        foreach (var repositoryIdHex in owners.ClaimableBy(fingerprint))
        {
            if (owners.Find(repositoryIdHex)?.ClaimTokenHex is not { } tokenHex)
            {
                continue;
            }

            issued[repositoryIdHex] = new ClaimCandidate(
                Convert.FromHexString(repositoryIdHex),
                Convert.FromHexString(tokenHex),
                RandomNumberGenerator.GetBytes(ReplicaClaimProof.NonceLength));
        }

        await PeerFrame.WriteAsync(
            stream, new ClaimChallenge([.. issued.Values]), cancellationToken).ConfigureAwait(false);

        var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            // A claimant that reads an empty challenge and hangs up is the
            // ordinary case, not a fault.
            return [];
        }

        if (frame.Value.Type != PeerMessageType.ClaimProof)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A {frame.Value.Type} is not part of a claim exchange (07 §5).");
        }

        var proof = ClaimProof.Read(frame.Value.Body);
        var claimed = new List<ClaimedReplica>();
        var moved = new List<string>();

        foreach (var answer in proof.Answers)
        {
            var repositoryIdHex = Convert.ToHexStringLower(answer.RepositoryId.Span);

            // Every check, and all three must hold. A proof for a candidate
            // this session did not issue is refused even if it verifies:
            // otherwise a claimant could replay one nonce forever.
            if (!issued.TryGetValue(repositoryIdHex, out var candidate)
                || owners.Find(repositoryIdHex)?.ClaimPublicKeyHex is not { } registered
                || !CryptographicOperations.FixedTimeEquals(
                    answer.ClaimPublicKey.Span, Convert.FromHexString(registered)))
            {
                continue;
            }

            // Rebuilt from this side's own copy of every field. Nothing the
            // claimant sent decides what it signed.
            var message = ReplicaClaimProof.Message(
                candidate.RepositoryId.Span,
                candidate.ClaimToken.Span,
                candidate.Nonce.Span,
                transcriptHash.Span,
                peer.Identity.PublicKey);

            if (!ReplicaClaimProof.Verify(answer.ClaimPublicKey.Span, message, answer.Signature.Span)
                || !owners.TryReattribute(repositoryIdHex, fingerprint))
            {
                continue;
            }

            moved.Add(repositoryIdHex);
            claimed.Add(new ClaimedReplica(
                answer.RepositoryId,
                await SetIdsAsync(replicasRoot, repositoryIdHex, cancellationToken).ConfigureAwait(false)));
        }

        if (moved.Count > 0)
        {
            // The operator is told, and retention stays refused until they
            // acknowledge it (06 §3). Reading is already available: a disaster
            // is when the far household is least reachable.
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Application.NoticeStore.Open(stateDirectory).Raise(
                "replica-claimed",
                $"Peer {fingerprint} proved the passphrase for {moved.Count} replica(s) held here and now owns "
                + "them. Restores are served already; ageing them is refused until you acknowledge this.",
                now);
        }

        await PeerFrame.WriteAsync(stream, new ClaimResult(claimed), cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <summary>
    /// The backup-set identifiers a replica's snapshots carry, read from the
    /// store's key namespace rather than from any manifest.
    /// </summary>
    private static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> SetIdsAsync(
        string replicasRoot, string repositoryIdHex, CancellationToken cancellationToken)
    {
        var path = Path.Combine(replicasRoot, repositoryIdHex);
        if (!Directory.Exists(path))
        {
            return [];
        }

        var store = new LocalFileSystemObjectStore(path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var setIds = new List<ReadOnlyMemory<byte>>();

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            // snapshots/<device-id>/<backup-set-id>/<snapshot-id>
            var parts = entry.Key.Value.Split('/');
            if (parts.Length < 4 || !seen.Add(parts[2]))
            {
                continue;
            }

            var setId = new byte[SetIdLength];
            if (Base32.TryDecode(parts[2], setId, out var written) && written == SetIdLength)
            {
                setIds.Add(setId);
            }

            if (setIds.Count == ClaimResult.MaximumSetIds)
            {
                break;
            }
        }

        return setIds;
    }
}
