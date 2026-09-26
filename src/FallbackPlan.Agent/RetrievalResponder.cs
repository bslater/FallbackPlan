using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination side of the retrieval payload (peer-protocol 07,
/// ADR-0041): an owner reads its own replica back. Everything served is the
/// owner's ciphertext returning home — nothing decrypts here — and the only
/// replicas served are those the attribution ledger assigns to the dialing
/// peer's pinned identity. A repository someone else stores here and one
/// never stored here refuse identically: which of the two it was is nobody's
/// business but the ledger's (no reconnaissance).
/// </summary>
internal static class RetrievalResponder
{
    /// <summary>Serves one retrieval session over an open peer stream.</summary>
    /// <param name="replicasRoot">Where replicas live, one directory per repository id.</param>
    /// <param name="stream">The open session stream, positioned after the retrieve-open.</param>
    /// <param name="peer">The authenticated peer.</param>
    /// <param name="owners">The replica attribution ledger (peer-protocol 05 §2).</param>
    /// <param name="open">The already-read opening request.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <param name="chunkPossessionNegotiated">Whether the session admits Merkle chunk challenges (07 §3.6).</param>
    public static async Task ServeAsync(
        string replicasRoot,
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        RetrieveOpen open,
        CancellationToken cancellationToken,
        bool chunkPossessionNegotiated = false)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);
        ThrowHelper.ThrowIfNull(open);

        try
        {
            if (open.FormatCapability != ReplicationInitiator.FormatCapability)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.FeatureUnsupported,
                    $"The requested repository format capability {open.FormatCapability} is not implemented.");
            }

            // An all-zero id opens the OWNER INVENTORY (07 §3.5): listings
            // answer the repository ids this peer owns here — how a hub that
            // lost its staging learns what to ask for. Nothing about other
            // peers' replicas is reachable this way.
            if (!open.RepositoryId.Span.ContainsAnyExcept((byte)0))
            {
                await ServeInventoryAsync(stream, peer, owners, cancellationToken).ConfigureAwait(false);
                return;
            }

            var repositoryIdHex = Convert.ToHexStringLower(open.RepositoryId.Span);
            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            if (!owners.OwnedBy(peer.Identity.Fingerprint).Contains(repositoryIdHex, StringComparer.Ordinal)
                || !Directory.Exists(replicaPath))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    "No replica of that repository is retrievable under this pairing.");
            }

            var replica = new LocalFileSystemObjectStore(replicaPath);
            await PeerFrame.WriteAsync(stream, new RetrieveReady(), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                // The owner simply closing the stream ends the session — the
                // replication payload's own termination shape (03 §3.4).
                var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                switch (frame.Value.Type)
                {
                    case PeerMessageType.RetrieveList:
                        await ServeListAsync(replica, stream, RetrieveList.Read(frame.Value.Body), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case PeerMessageType.RetrieveRead:
                        await ServeReadAsync(replica, stream, RetrieveRead.Read(frame.Value.Body), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case PeerMessageType.MerkleChallenge:
                        if (!chunkPossessionNegotiated)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.FeatureUnsupported,
                                "A Merkle challenge arrived without the chunk-possession feature in effect.");
                        }

                        await ServeMerkleChallengeAsync(
                            replica, open.RepositoryId, stream, MerkleChallenge.Read(frame.Value.Body),
                            cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed,
                            $"A {frame.Value.Type} is not part of an open retrieval session (07 §3).");
                }
            }
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

    private static async Task ServeInventoryAsync(
        Stream stream, PeerGrant peer, Application.ReplicaOwnerStore owners, CancellationToken cancellationToken)
    {
        await PeerFrame.WriteAsync(stream, new RetrieveReady(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            if (frame.Value.Type != PeerMessageType.RetrieveList)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A {frame.Value.Type} is not part of an owner-inventory session (07 §3.5).");
            }

            _ = RetrieveList.Read(frame.Value.Body);
            var owned = owners.OwnedBy(peer.Identity.Fingerprint)
                .Order(StringComparer.Ordinal)
                .Take(RetrieveListPage.MaximumKeys)
                .ToList();
            await PeerFrame.WriteAsync(
                stream,
                new RetrieveListPage(owned, [.. owned.Select(_ => 0UL)], More: false),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ServeListAsync(
        LocalFileSystemObjectStore replica, Stream stream, RetrieveList request, CancellationToken cancellationToken)
    {
        if (!ObjectPrefix.TryParse(request.Prefix, out var prefix))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "The listing prefix would never name a stored object.");
        }

        var keys = new List<string>(capacity: 128);
        var lengths = new List<ulong>(capacity: 128);
        var more = false;
        var options = request.After.Length == 0
            ? ListOptions.Default
            : new ListOptions { ResumeAfter = request.After };

        await foreach (var entry in replica.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            if (keys.Count >= RetrieveListPage.MaximumKeys)
            {
                more = true;
                break;
            }

            keys.Add(entry.Key.Value);
            lengths.Add((ulong)entry.Length);
        }

        await PeerFrame.WriteAsync(stream, new RetrieveListPage(keys, lengths, more), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ServeReadAsync(
        LocalFileSystemObjectStore replica, Stream stream, RetrieveRead request, CancellationToken cancellationToken)
    {
        if (!ObjectKey.TryParse(request.Key, out var key))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "The read names something that is not an object key.");
        }

        var metadata = await replica.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } found)
        {
            await PeerFrame.WriteAsync(
                stream, new RetrieveData(Found: false, 0, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Length == 0 || found.Length == 0)
        {
            await PeerFrame.WriteAsync(
                stream, new RetrieveData(Found: true, (ulong)found.Length, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Clamped to the object: a read past the end answers what exists
        // rather than refusing, so the owner never has to guess lengths.
        var offset = (long)Math.Min(request.Offset, (ulong)found.Length);
        var length = (int)Math.Min(request.Length, (ulong)(found.Length - offset));
        if (length == 0)
        {
            await PeerFrame.WriteAsync(
                stream, new RetrieveData(Found: true, (ulong)found.Length, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var buffer = new byte[length];
        using (var read = await replica.OpenReadAsync(key, new ObjectRange(offset, length), cancellationToken)
            .ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                await PeerFrame.WriteAsync(
                    stream, new RetrieveData(Found: false, 0, ReadOnlyMemory<byte>.Empty), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await read.Content!.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        await PeerFrame.WriteAsync(
            stream, new RetrieveData(Found: true, (ulong)found.Length, buffer), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Answers one Merkle challenge (07 §3.6): the challenged leaf's bytes
    /// and the sibling hashes that carry them to the root.
    /// </summary>
    /// <remarks>
    /// The whole blob is read from local disk to build the path, and that is
    /// the cost this message moves rather than removes — a disk read here
    /// instead of an uplink's worth of bytes there. No key is involved: the
    /// tree is over ciphertext this side already holds, exactly as
    /// <c>ReplicationResponder</c>'s range challenge is.
    /// </remarks>
    private static async Task ServeMerkleChallengeAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> sessionRepositoryId,
        Stream stream,
        MerkleChallenge request,
        CancellationToken cancellationToken)
    {
        if (!request.RepositoryId.Span.SequenceEqual(sessionRepositoryId.Span))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A Merkle challenge names a repository other than the one this session opened.");
        }

        if (!ObjectKey.TryParse(request.Key, out var key))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "The challenge names something that is not an object key.");
        }

        // The lifecycle namespaces are off limits to a challenge for the
        // reason 04 §4.1 gives: they are the destination's own record of
        // what it was told to delete, not the owner's content.
        if (request.Key.StartsWith("tombstones/", StringComparison.Ordinal)
            || request.Key.StartsWith("leases/", StringComparison.Ordinal))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A Merkle challenge may not name a lifecycle object.");
        }

        var metadata = await replica.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata.Metadata is not { } found || found.Length <= FooterLocator.Length)
        {
            await CannotProveAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The commitment's preimage stops short of the locator (05 §5.2),
        // the same bytes the flat digest names.
        var preimageLength = found.Length - FooterLocator.Length;
        if (request.LeafIndex >= (uint)BlobMerkle.LeafCount(preimageLength))
        {
            // A leaf beyond this copy is not an error — it is the answer,
            // and it is the interesting one.
            await CannotProveAsync(stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        var leafOffset = BlobMerkle.LeafOffset((int)request.LeafIndex);
        var leafLength = BlobMerkle.LeafLength(preimageLength, (int)request.LeafIndex);
        var leaf = new byte[leafLength];

        using var accumulator = new BlobMerkleAccumulator();
        using (var read = await replica.OpenReadAsync(
                   key, new ObjectRange(0, preimageLength), cancellationToken).ConfigureAwait(false))
        {
            if (read.Outcome != OpenReadOutcome.Found)
            {
                await CannotProveAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Streamed, never buffered: a blob runs to 512 MiB and the only
            // bytes this holds are the one leaf it must send back.
            var buffer = new byte[64 * 1024];
            var position = 0L;
            while (position < preimageLength)
            {
                var wanted = (int)Math.Min(buffer.Length, preimageLength - position);
                var taken = await read.Content!.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);
                if (taken == 0)
                {
                    await CannotProveAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                accumulator.Append(buffer.AsSpan(0, taken));

                var overlapStart = Math.Max(position, leafOffset);
                var overlapEnd = Math.Min(position + taken, leafOffset + leafLength);
                if (overlapEnd > overlapStart)
                {
                    buffer.AsSpan((int)(overlapStart - position), (int)(overlapEnd - overlapStart))
                        .CopyTo(leaf.AsSpan((int)(overlapStart - leafOffset)));
                }

                position += taken;
            }
        }

        var (leafHashes, _) = accumulator.CompleteAndReset();
        var path = BlobMerkle.AuthenticationPath(leafHashes, (int)request.LeafIndex);

        await PeerFrame.WriteAsync(
            stream,
            new MerkleProof(true, leaf, [.. path.Select(step => (ReadOnlyMemory<byte>)step)]),
            cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask CannotProveAsync(Stream stream, CancellationToken cancellationToken) =>
        PeerFrame.WriteAsync(
            stream, new MerkleProof(false, ReadOnlyMemory<byte>.Empty, []), cancellationToken);
}
