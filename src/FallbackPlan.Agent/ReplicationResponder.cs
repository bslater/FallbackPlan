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
    public sealed record Outcome(string RepositoryId, long Committed);

    /// <summary>Serves one replication session from a source.</summary>
    /// <param name="replicasRoot">The directory under which per-repository replica stores live.</param>
    /// <param name="spoolRoot">A scratch directory for objects being received.</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="cancellationToken">Cancels serving.</param>
    /// <returns>What was received.</returns>
    public static async Task<Outcome> ServeAsync(
        string replicasRoot, string spoolRoot, Stream stream, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);
        ThrowHelper.ThrowIfNull(stream);

        try
        {
            var offer = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationOffer, ReplicationOffer.Read, cancellationToken)
                .ConfigureAwait(false);

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
            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            Directory.CreateDirectory(replicaPath);
            Directory.CreateDirectory(spoolRoot);
            var replica = new LocalFileSystemObjectStore(replicaPath);

            await SendInventoryAsync(replica, stream, cancellationToken).ConfigureAwait(false);
            var committed = await ReceiveAsync(replica, spoolRoot, stream, cancellationToken).ConfigureAwait(false);

            await PeerFrame.WriteAsync(stream, new ReplicationAck((ulong)committed), cancellationToken)
                .ConfigureAwait(false);
            return new Outcome(repositoryIdHex, committed);
        }
        catch (PeerProtocolException exception)
        {
            await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
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
        LocalFileSystemObjectStore replica, string spoolRoot, Stream stream, CancellationToken cancellationToken)
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
                        current = new Incoming(spoolRoot, header.Key, header.Length);
                        if (current.Complete)
                        {
                            await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                            committed++;
                            current.Dispose();
                            current = null;
                        }

                        break;

                    case PeerMessageType.ReplicationChunk:
                        if (current is null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "A chunk arrived with no object to append it to.");
                        }

                        await current.AppendAsync(ReplicationChunk.Read(body), cancellationToken).ConfigureAwait(false);
                        if (current.Complete)
                        {
                            await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                            committed++;
                            current.Dispose();
                            current = null;
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
