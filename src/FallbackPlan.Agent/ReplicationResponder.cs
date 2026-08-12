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
    public sealed record Outcome(string RepositoryId, long Committed, PeeringTermination? Termination = null);

    /// <summary>Serves one replication session from a source.</summary>
    /// <param name="replicasRoot">The directory under which per-repository replica stores live.</param>
    /// <param name="spoolRoot">A scratch directory for objects being received.</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="peer">The authenticated source's grant — its terms are what this side enforces (05 §1).</param>
    /// <param name="owners">The replica attribution store (05 §2).</param>
    /// <param name="cancellationToken">Cancels serving.</param>
    /// <returns>What was received.</returns>
    public static async Task<Outcome> ServeAsync(
        string replicasRoot, string spoolRoot, Stream stream,
        Protocol.PeerGrant peer, FallbackPlan.Application.ReplicaOwnerStore owners,
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

            await SendInventoryAsync(replica, stream, cancellationToken).ConfigureAwait(false);
            var committed = await ReceiveAsync(replica, spoolRoot, stream, quota, usage, cancellationToken)
                .ConfigureAwait(false);

            await PeerFrame.WriteAsync(stream, new ReplicationAck((ulong)committed), cancellationToken)
                .ConfigureAwait(false);
            return new Outcome(repositoryIdHex, committed);
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

    private static async Task SendInventoryAsync(
        LocalFileSystemObjectStore replica, Stream stream, CancellationToken cancellationToken)
    {
        var page = new List<string>(ReplicationInventory.MaximumKeys);
        await foreach (var entry in replica.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            page.Add(entry.Key.Value);
            if (page.Count == ReplicationInventory.MaximumKeys)
            {
                await PeerFrame.WriteAsync(stream, new ReplicationInventory([.. page], More: true), cancellationToken)
                    .ConfigureAwait(false);
                page.Clear();
            }
        }

        // The final (or only) page carries whatever remains and closes the inventory.
        await PeerFrame.WriteAsync(stream, new ReplicationInventory([.. page], More: false), cancellationToken)
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
            await replica.PutAsync(
                objectKey,
                _ => new ValueTask<Stream>(File.OpenRead(_path)),
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
