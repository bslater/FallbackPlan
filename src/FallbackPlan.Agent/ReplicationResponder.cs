using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination half of replication (specification peer-protocol 03): over an
/// open peer session, accept a source's offer, declare what it already holds,
/// and receive the objects it lacks — committing each whole, or not at all
/// (03 §5). It writes ciphertext it cannot read into a replica store it chooses
/// the location of (03 §8).
/// </summary>
internal static class ReplicationResponder
{
    /// <summary>The result of serving one replication session.</summary>
    /// <param name="RepositoryId">The repository whose objects were received, hex.</param>
    /// <param name="Committed">How many objects were committed.</param>
    /// <param name="Termination">Present when the peer announced the peering's end instead of replicating (01 §3).</param>
    /// <param name="RetentionDeleted">Objects deleted under a retention instruction this session (06).</param>
    public sealed record Outcome(
        string RepositoryId, long Committed, PeeringTermination? Termination = null, long RetentionDeleted = 0);

    /// <summary>Serves one replication session from a source.</summary>
    /// <param name="replicasRoot">The directory under which per-repository replica stores live.</param>
    /// <param name="spoolRoot">A scratch directory for objects being received.</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="peer">The authenticated source's grant — its terms are what this side enforces (05 §1).</param>
    /// <param name="owners">The replica attribution store (05 §2).</param>
    /// <param name="retentionNegotiated">Whether the session's features admit a retention instruction (06 §1).</param>
    /// <param name="verificationNegotiated">Whether the session's features admit verification challenges (04 §1).</param>
    /// <param name="cancellationToken">Cancels serving.</param>
    /// <returns>What was received.</returns>
    public static async Task<Outcome> ServeAsync(
        string replicasRoot, string spoolRoot, Stream stream,
        Protocol.PeerGrant peer, FallbackPlan.Application.ReplicaOwnerStore owners,
        bool retentionNegotiated,
        bool verificationNegotiated,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);

        try
        {
            // The first frame is the offer — or, under the termination-notice
            // feature, the announcement that there will never be another
            // (ADR-0030 Amendment 2). The caller raises the durable notice;
            // this layer only recognises the message.
            var first = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                ?? throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "The peer closed before sending a replication offer.");
            if (first.Type == PeerMessageType.PeeringTermination)
            {
                return new Outcome(string.Empty, 0, PeeringTermination.Read(first.Body));
            }

            if (first.Type != PeerMessageType.ReplicationOffer)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"Expected a replication offer; the peer sent a {first.Type}.");
            }

            var offer = ReplicationOffer.Read(first.Body);

            if (offer.FormatCapability != ReplicationInitiator.FormatCapability)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.FeatureUnsupported,
                    $"The offered repository format capability {offer.FormatCapability} is not implemented.");
            }

            if (!string.Equals(offer.Scope, "all", StringComparison.Ordinal))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"The scope '{offer.Scope}' is not one this build understands.");
            }

            var repositoryIdHex = Convert.ToHexStringLower(offer.RepositoryId.Span);

            // The attribution is what makes the quota's denominator — "the
            // total this peer stores here" — computable across sessions
            // (05 §2). A repository another peer owns here is refused rather
            // than counted against the wrong household's ledger.
            if (!owners.TryAttribute(repositoryIdHex, peer.Identity.Fingerprint))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    $"Repository {repositoryIdHex} is already stored here for another peer.");
            }

            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            try
            {
                Directory.CreateDirectory(replicaPath);
                Directory.CreateDirectory(spoolRoot);
            }
            catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
            {
                throw CannotStore(storage);
            }

            var replica = new LocalFileSystemObjectStore(replicaPath);

            // Quota 0 declares no ceiling (05 §1); anything above it bounds
            // the peer's committed bytes across every repository it owns
            // here, so usage is summed before the first object crosses.
            var quota = peer.Terms.QuotaBytes;
            var usage = quota > 0
                ? await UsageAsync(replicasRoot, owners.OwnedBy(peer.Identity.Fingerprint), cancellationToken)
                    .ConfigureAwait(false)
                : 0UL;

            // The same two numbers the boundary stop is enforced from, told
            // to the source up front so it learns the ceiling is close before
            // a push runs into it rather than only when one does (05 §4).
            await SendInventoryAsync(
                replica, stream, quota > 0 ? quota - Math.Min(usage, quota) : null, cancellationToken)
                .ConfigureAwait(false);
            var committed = await ReceiveAsync(replica, spoolRoot, stream, quota, usage, cancellationToken)
                .ConfigureAwait(false);

            await PeerFrame.WriteAsync(stream, new ReplicationAck((ulong)committed), cancellationToken)
                .ConfigureAwait(false);

            var retentionDeleted = await ServeAfterAckAsync(
                replica, offer.RepositoryId, peer, retentionNegotiated, verificationNegotiated,
                stream, cancellationToken)
                .ConfigureAwait(false);

            return new Outcome(repositoryIdHex, committed, RetentionDeleted: retentionDeleted);
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
    /// Serves whatever follows the acknowledgement: an optional retention
    /// instruction (peer-protocol 06), then any number of verification
    /// challenges (04) — in that order, because verifying a key the same
    /// session then deletes proves nothing anyone keeps. The session ends
    /// when the peer closes.
    /// </summary>
    private static async Task<long> ServeAfterAckAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        Protocol.PeerGrant peer,
        bool retentionNegotiated,
        bool verificationNegotiated,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var retentionDeleted = 0L;
        var retentionServed = false;
        var challengeServed = false;

        while (true)
        {
            // The session may simply end here — everything after the
            // acknowledgement is optional.
            var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return retentionDeleted;
            }

            switch (frame.Value.Type)
            {
                case PeerMessageType.RetentionOffer when !retentionServed && !challengeServed:
                    if (!retentionNegotiated)
                    {
                        // Sending a gated message outside the negotiated
                        // intersection is a violation, not a capability
                        // probe (02 §6).
                        throw new PeerProtocolException(
                            PeerRefusalReason.FeatureUnsupported,
                            "A retention instruction arrived without the retention-instruction feature in effect.");
                    }

                    retentionDeleted = await ServeRetentionAsync(
                        replica, offeredRepositoryId, peer, frame.Value.Body, stream, cancellationToken)
                        .ConfigureAwait(false);
                    retentionServed = true;
                    break;

                case PeerMessageType.VerificationChallenge:
                    if (!verificationNegotiated)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.FeatureUnsupported,
                            "A verification challenge arrived without the destination-verification feature in effect.");
                    }

                    await ServeChallengeAsync(replica, offeredRepositoryId, frame.Value.Body, stream, cancellationToken)
                        .ConfigureAwait(false);
                    challengeServed = true;
                    break;

                default:
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed,
                        $"A {frame.Value.Type} is not permitted after the replication acknowledgement.");
            }
        }
    }

    /// <summary>
    /// Answers one keyed random-range challenge (peer-protocol 04): read
    /// exactly the named range from the stored copy and prove it, or answer
    /// honestly that this side cannot — which is the answer the challenge
    /// exists to force into the open. No keys are involved: the proof is
    /// over ciphertext this side already holds.
    /// </summary>
    private static async Task ServeChallengeAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        System.Formats.Cbor.CborReader body,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var challenge = VerificationChallenge.Read(body);

        if (!challenge.RepositoryId.Span.SequenceEqual(offeredRepositoryId.Span))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A verification challenge names a repository other than the one this session replicated.");
        }

        if (challenge.Key.StartsWith("tombstones/", StringComparison.Ordinal)
            || challenge.Key.StartsWith("leases/", StringComparison.Ordinal))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A verification challenge may not name '{challenge.Key}' — lifecycle keys never replicate (04 §4.1).");
        }

        // The exact challenged range, or null for every honest inability
        // (04 §4.2) — the same reader the source uses on its own bytes, so
        // the two ends cannot disagree about what "cannot produce it" means.
        var bytes = await Replication.RangeReader.ReadAsync(
            replica, challenge.Key, challenge.Offset, challenge.Length, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            await PeerFrame.WriteAsync(
                stream, new VerificationProof(Held: false, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var proof = RangeChallenge.Compute(
            challenge.ChallengeKey.Span, challenge.Nonce.Span, challenge.Key,
            challenge.Offset, challenge.Length, bytes);
        await PeerFrame.WriteAsync(stream, new VerificationProof(Held: true, proof), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a retention instruction (peer-protocol 06): the commander
    /// computed, this side deletes exactly what it is told — bounded below
    /// by the granted retention floor, which is the one safeguard that holds
    /// when the hub is compromised. The floor check needs no decryption:
    /// snapshot objects are counted by prefix, so a spoke that cannot read a
    /// single manifest can still refuse to breach it.
    /// </summary>
    private static async Task<long> ServeRetentionAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        Protocol.PeerGrant peer,
        System.Formats.Cbor.CborReader firstBody,
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Every page is read before anything is deleted: the floor check is
        // over the whole instruction, or a piecewise pass could breach it in
        // total (06 §4.1).
        var drops = new List<string>();
        var page = RetentionOffer.Read(firstBody);
        while (true)
        {
            if (!page.RepositoryId.Span.SequenceEqual(offeredRepositoryId.Span))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    "A retention page names a repository other than the one this session replicated.");
            }

            foreach (var key in page.Keys)
            {
                if (key is "repository-format"
                    || key.StartsWith("keys/", StringComparison.Ordinal)
                    || key.StartsWith("tombstones/", StringComparison.Ordinal)
                    || key.StartsWith("leases/", StringComparison.Ordinal))
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.TermsRefused,
                        $"A retention instruction may not name '{key}' — identity and lifecycle keys are never deletable by instruction (06 §3).");
                }

                drops.Add(key);
            }

            if (!page.More)
            {
                break;
            }

            page = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.RetentionOffer, RetentionOffer.Read, cancellationToken).ConfigureAwait(false);
        }

        // The floor (06 §3, FR-GC-010): counted on ciphertext, refused whole.
        var heldSnapshots = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in replica.ListAsync(
            ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            heldSnapshots.Add(entry.Key.Value);
        }

        var droppedSnapshots = drops.Count(key => heldSnapshots.Contains(key));
        var remaining = heldSnapshots.Count - droppedSnapshots;
        if (remaining < peer.Terms.RetentionFloorGenerations)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.TermsRefused,
                $"The instruction would leave {remaining} snapshot(s); this grant's retention floor is "
                + $"{peer.Terms.RetentionFloorGenerations} (06 §3).");
        }

        var deleted = 0L;
        foreach (var key in drops)
        {
            var outcome = await replica.DeleteAsync(
                Storage.Abstractions.ObjectKey.Parse(key), DeleteConditions.None, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Outcome == DeleteOutcome.Deleted)
            {
                deleted++;
            }
        }

        await PeerFrame.WriteAsync(stream, new RetentionAck((ulong)deleted), cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static async Task SendInventoryAsync(
        LocalFileSystemObjectStore replica, Stream stream, ulong? headroom, CancellationToken cancellationToken)
    {
        var page = new List<string>(ReplicationInventory.MaximumKeys);
        await foreach (var entry in replica.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            page.Add(entry.Key.Value);
            if (page.Count == ReplicationInventory.MaximumKeys)
            {
                await PeerFrame.WriteAsync(
                    stream, new ReplicationInventory([.. page], More: true, headroom), cancellationToken)
                    .ConfigureAwait(false);
                page.Clear();
            }
        }

        // The final (or only) page carries whatever remains and closes the inventory.
        await PeerFrame.WriteAsync(
            stream, new ReplicationInventory([.. page], More: false, headroom), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<long> ReceiveAsync(
        LocalFileSystemObjectStore replica, string spoolRoot, Stream stream,
        ulong quota, ulong usage, CancellationToken cancellationToken)
    {
        var committed = 0L;
        Incoming? current = null;
        try
        {
            while (true)
            {
                var (type, body) = await ReplicationWire.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                switch (type)
                {
                    case PeerMessageType.ReplicationComplete:
                        ReplicationComplete.Read(body);
                        if (current is not null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "Replication completed with an object still unfinished.");
                        }

                        return committed;

                    case PeerMessageType.ReplicationObject:
                        if (current is not null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "A new object began before the previous one finished.");
                        }

                        var header = ReplicationObject.Read(body);

                        // The quota check runs here, where the length is
                        // declared and nothing is yet spooled — the clean
                        // stop at the object boundary (05 §3). Everything
                        // committed so far stays committed.
                        if (quota > 0 && usage + header.Length > quota)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.TermsRefused,
                                $"The quota of {quota} bytes is exhausted: {usage} bytes are stored"
                                + $" and the next object declares {header.Length}.");
                        }

                        try
                        {
                            current = new Incoming(spoolRoot, header.Key, header.Length);
                            if (current.Complete)
                            {
                                await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                                committed++;
                                usage += header.Length;
                                current.Dispose();
                                current = null;
                            }
                        }
                        catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
                        {
                            throw CannotStore(storage);
                        }

                        break;

                    case PeerMessageType.ReplicationChunk:
                        if (current is null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "A chunk arrived with no object to append it to.");
                        }

                        try
                        {
                            await current.AppendAsync(ReplicationChunk.Read(body), cancellationToken).ConfigureAwait(false);
                            if (current.Complete)
                            {
                                await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                                committed++;
                                usage += current.Length;
                                current.Dispose();
                                current = null;
                            }
                        }
                        catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
                        {
                            throw CannotStore(storage);
                        }

                        break;

                    default:
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed, $"A {type} is not part of the replication payload here.");
                }
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// The peer's committed bytes across every repository attributed to it —
    /// the quota's denominator (05 §1). Summed from the stores themselves, so
    /// losing no separate counter can ever disagree with what is held.
    /// </summary>
    private static async ValueTask<ulong> UsageAsync(
        string replicasRoot, IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        var total = 0UL;
        foreach (var repositoryIdHex in repositories)
        {
            var path = Path.Combine(replicasRoot, repositoryIdHex);
            if (!Directory.Exists(path))
            {
                continue;
            }

            var store = new LocalFileSystemObjectStore(path);
            await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                total += (ulong)entry.Length;
            }
        }

        return total;
    }

    /// <summary>
    /// Disk trouble spoken as itself: <c>storage_exhausted</c>, never
    /// <c>terms_refused</c> — the quota said yes and the hardware said no,
    /// and the two send the human to different fixes (05 §4).
    /// </summary>
    private static PeerProtocolException CannotStore(Exception storage) =>
        new(PeerRefusalReason.StorageExhausted, $"The destination cannot store: {storage.Message}", storage);

    /// <summary>An object being received: spooled to a temp file, committed whole (03 §5).</summary>
    private sealed class Incoming : IDisposable
    {
        private readonly string _key;
        private readonly ulong _length;
        private readonly string _path;
        private readonly FileStream _spool;
        private ulong _received;

        public Incoming(string spoolRoot, string key, ulong length)
        {
            _key = key;
            _length = length;
            _path = Path.Combine(spoolRoot, Guid.NewGuid().ToString("n"));
            _spool = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite);
        }

        public bool Complete => _received == _length;

        public ulong Length => _length;

        public async ValueTask AppendAsync(ReplicationChunk chunk, CancellationToken cancellationToken)
        {
            if (chunk.Offset != _received)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"A chunk at offset {chunk.Offset} arrived; {_received} was expected.");
            }

            var next = _received + (ulong)chunk.Bytes.Length;
            if (next > _length)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "An object's bytes overran the length it declared.");
            }

            await _spool.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
            _received = next;
        }

        public async ValueTask CommitAsync(LocalFileSystemObjectStore replica, CancellationToken cancellationToken)
        {
            if (!ObjectKey.TryParse(_key, out var objectKey))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"'{_key}' is not a valid object key.");
            }

            await _spool.FlushAsync(cancellationToken).ConfigureAwait(false);

            // A create-if-absent write makes commit atomic and re-runs idempotent
            // (03 §5): an object already held is identical to the one offered.
            // The read handle must share with _spool, which is still open with
            // write access — a default-share open is a sharing violation on
            // Windows, and the commit would fail for every object ever spooled.
            await replica.PutAsync(
                objectKey,
                _ => new ValueTask<Stream>(
                    new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)),
                PutConditions.IfNotExists,
                cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _spool.Dispose();
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException)
            {
                // A leftover spool file is scratch, not state.
            }
        }
    }
}
