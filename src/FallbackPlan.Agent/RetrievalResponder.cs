using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;

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
    public static async Task ServeAsync(
        string replicasRoot,
        Stream stream,
        PeerGrant peer,
        Application.ReplicaOwnerStore owners,
        RetrieveOpen open,
        CancellationToken cancellationToken)
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

            var replica = StoreComposition.OpenLocal(replicaPath);
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
        IObjectStore replica, Stream stream, RetrieveList request, CancellationToken cancellationToken)
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
        IObjectStore replica, Stream stream, RetrieveRead request, CancellationToken cancellationToken)
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
}
