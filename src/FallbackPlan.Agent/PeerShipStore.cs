using System.Runtime.CompilerServices;
using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// The peer write adapter (ADR-0058): a paired peer destination as an
/// <see cref="IObjectStore"/> a direct-ship run (ADR-0046) writes through, so
/// a set whose durability lives at a friend's house captures into it directly
/// rather than being told that peer shipping follows later.
/// </summary>
/// <remarks>
/// <para>
/// It is one live replication push session (peer-protocol 03) held open for
/// the length of a run: the offer and the destination's inventory at
/// <see cref="OpenAsync"/>, one <see cref="ReplicationObject"/> and its chunks
/// per <see cref="PutAsync"/>, and the completion and its acknowledgement at
/// <see cref="CompleteAsync"/>. A buffer-then-push shape would have been
/// simpler and would have reintroduced the local copy of the backup that
/// ADR-0046 exists to remove, which is why the session is live.
/// </para>
/// <para>
/// A session is one stream and the sink ships to its destinations
/// concurrently, so every write is serialised here. That is not a
/// pessimisation of something that was parallel: a single peer's uplink is the
/// bottleneck this is queueing for, and the other destinations are not waiting
/// on this one.
/// </para>
/// <para>
/// Reads travel a second, lazily-dialled retrieval session (peer-protocol 07,
/// ADR-0041) — the replication exchange has no read in it — and a key the
/// opening inventory did not list is answered absent without dialling at all.
/// A peer too old to serve retrieval can receive and cannot be read back
/// through this store; its replica is still whole, and the restore-source path
/// is how it is read.
/// </para>
/// <para>
/// Everything that goes wrong on the wire leaves here as an
/// <see cref="IOException"/>. That is deliberate rather than lazy: the sink's
/// drop rule — one destination's failure is that destination's, never the
/// run's, while a sibling remains — is written against the storage exceptions,
/// and a peer that dies mid-run must be dropped by the same rule as a disk
/// that fills.
/// </para>
/// </remarks>
internal sealed class PeerShipStore : IObjectStore, IAsyncDisposable
{
    private readonly ServiceRuntime _runtime;
    private readonly DestinationConfiguration _destination;
    private readonly ReadOnlyMemory<byte> _repositoryId;
    private readonly PeerKeypair _keypair;
    private readonly PeerTlsConnection _connection;
    private readonly PeerSession _session;
    private readonly HashSet<string> _held;

    // The receipt is checked against what this session actually put on the
    // wire, so the sent keys are kept apart from the inventory: `_held` starts
    // as what the peer declared and grows with this session's creates, and a
    // receipt listing something the run never sent is a different lie from one
    // that miscounts.
    private readonly HashSet<string> _sentKeys = [];
    private readonly int _heldAtStart;
    private readonly Lock _inventory = new();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);

    private long _sent;
    private bool _closed;
    private bool _faulted;
    private PeerRetrievalClient? _retrieval;
    private PeerRetrievalObjectStore? _reads;
    private bool _retrievalRefused;

    private PeerShipStore(
        ServiceRuntime runtime,
        DestinationConfiguration destination,
        ReadOnlyMemory<byte> repositoryId,
        PeerKeypair keypair,
        PeerTlsConnection connection,
        PeerSession session,
        HashSet<string> held)
    {
        _runtime = runtime;
        _destination = destination;
        _repositoryId = repositoryId;
        _keypair = keypair;
        _connection = connection;
        _session = session;
        _held = held;
        _heldAtStart = held.Count;
    }

    /// <summary>How many objects this session has put on the wire.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>The destination's configured name, for the ledger and the log.</summary>
    public string DestinationName => _destination.Name;

    /// <inheritdoc/>
    public StoreCapabilities Capabilities { get; } = new() { RangedReads = true };

    /// <summary>
    /// Dials the destination, offers the repository, and reads the inventory
    /// that says what it already holds — everything before the first object.
    /// </summary>
    /// <param name="runtime">The service, for the state directory holding keys and grants.</param>
    /// <param name="destination">The peer destination's declaration.</param>
    /// <param name="repositoryId">The repository being shipped (16 bytes).</param>
    /// <param name="claimPublicKey">
    /// The installation's claim public key
    /// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1),
    /// recorded by the destination at the same first attribution; empty when
    /// this source has none.
    /// </param>
    /// <param name="reclaimPublicKey">
    /// The repository's reclaim public key (ADR-0055 §5), recorded by the
    /// destination at first attribution; empty when this source has none.
    /// </param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <exception cref="IOException">The peer is unreachable, refused the session, or refused the repository.</exception>
    public static async Task<PeerShipStore> OpenAsync(
        ServiceRuntime runtime,
        DestinationConfiguration destination,
        ReadOnlyMemory<byte> repositoryId,
        ReadOnlyMemory<byte> reclaimPublicKey,
        ReadOnlyMemory<byte> claimPublicKey,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(destination);

        if (!PeerAddress.TryResolve(runtime, destination, out var resolved, out var refusal))
        {
            throw new IOException(refusal ?? $"Destination '{destination.Name}' has no dialable address.");
        }

        var (grants, grant, host, port) = resolved!;
        var keypair = PeerKeypairStore.Open(runtime.Options.StateDirectory);
        PeerTlsConnection? connection = null;
        try
        {
            connection = await PeerTlsConnection.DialAsync(host, port, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            // A destination that will not prove possession is refused before a
            // byte crosses, exactly as on the fan-out's push (04 §1,
            // FR-VER-006) — the capture's own path must not be the loose one.
            var session = await PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                requiredFeatures: destination.RequiresVerification ? FanOut.VerificationRequirement : null,
                logger: null,
                offeredFeatures: ShipFeatures,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // The peer's hello carries its current terms; adopting them here
            // keeps one account of what it lends, whichever path reached it
            // (05 §6).
            if (session.TheirTerms is { } offered)
            {
                _ = grants.ApplyTerms(grant.Identity, offered);
            }

            await PeerFrame.WriteAsync(
                session.Stream,
                new ReplicationOffer(
                    repositoryId, ReplicationInitiator.FormatCapability, "all", reclaimPublicKey,
                    claimPublicKey),
                cancellationToken).ConfigureAwait(false);

            var held = await ReadInventoryAsync(session.Stream, cancellationToken).ConfigureAwait(false);
            return new PeerShipStore(runtime, destination, repositoryId, keypair, connection, session, held);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            keypair.Dispose();
            throw exception as IOException ?? Fault(destination.Name, exception);
        }
        catch (OperationCanceledException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            keypair.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What a ship session offers: everything this build speaks except
    /// resumption.
    /// </summary>
    /// <remarks>
    /// A resumed transfer finishes an object the source still holds
    /// ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)), and a
    /// direct-ship run holds none: its objects are sealed as they are produced
    /// and a run cut mid-blob seals a differently-identified blob next time, so
    /// a prefix staged here could never be finished. Offering the feature would
    /// have the destination keep those prefixes for a week, charged to this
    /// peer's quota, waiting for a second half that is never sent.
    /// </remarks>
    private static IReadOnlyList<string> ShipFeatures { get; } =
        [.. PeerSessionNegotiation.SupportedFeatures.Where(feature => !string.Equals(
            feature, PeerSessionNegotiation.PartialObjectResumeFeature, StringComparison.Ordinal))];

    /// <summary>
    /// Ships one object, or answers that the destination already holds it.
    /// </summary>
    /// <remarks>
    /// The condition is not consulted, because the replication exchange has
    /// only one: a destination commits an object it lacks and keeps the one it
    /// has (03 §5). That matches <see cref="PutConditions.IfNotExists"/>, and
    /// the format's objects are immutable, so there is no overwrite for it to
    /// be the wrong answer to.
    /// </remarks>
    /// <inheritdoc/>
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(openContent);

        if (Holds(key.Value))
        {
            return new PutResult(PutOutcome.AlreadyExists);
        }

        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate: two puts of one key can race the
            // check above, and the second must not put a duplicate object on a
            // stream whose count the acknowledgement is compared against.
            if (Holds(key.Value))
            {
                return new PutResult(PutOutcome.AlreadyExists);
            }

            ThrowIfUnusable();

            var content = await openContent(cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                await SendAsync(key, content, cancellationToken).ConfigureAwait(false);
            }

            lock (_inventory)
            {
                _held.Add(key.Value);
                _sentKeys.Add(key.Value);
            }

            Interlocked.Increment(ref _sent);
            return new PutResult(PutOutcome.Created);
        }
        catch (OperationCanceledException)
        {
            // A cancelled write leaves the stream mid-object as surely as a
            // failed one does.
            _faulted = true;
            throw;
        }
        catch (Exception exception)
        {
            _faulted = true;
            throw exception as IOException ?? Fault(_destination.Name, exception);
        }
        finally
        {
            _wire.Release();
        }
    }

    /// <summary>
    /// Closes the exchange: the completion, the destination's acknowledgement,
    /// and the check that the two counts agree.
    /// </summary>
    /// <remarks>
    /// The check is the same hard fault the fan-out's push makes of it. A
    /// destination that acknowledges fewer objects than it was sent, without
    /// refusing, has either a bug or a desynchronised stream, and in both cases
    /// the objects this run believes are there may not be — so the run records
    /// this destination as failed rather than as holding the capture.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>
    /// How many objects the destination acknowledged committing, the receipt
    /// it signed for them, and why one that arrived was not believed — the
    /// last two null when the destination sent none, which is a fact about an
    /// older peer and never a fault.
    /// </returns>
    /// <exception cref="IOException">The peer refused, went away, or under-acknowledged.</exception>
    public async Task<CompletedShipment> CompleteAsync(CancellationToken cancellationToken)
    {
        await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnusable();
            _closed = true;

            var sent = Interlocked.Read(ref _sent);
            await PeerFrame.WriteAsync(
                _session.Stream, new ReplicationComplete((ulong)sent), cancellationToken).ConfigureAwait(false);
            var ack = await ReplicationWire.ReadAsync(
                _session.Stream, PeerMessageType.ReplicationAck, ReplicationAck.Read, cancellationToken)
                .ConfigureAwait(false);

            if ((long)ack.Count < sent)
            {
                throw new IOException(
                    $"Peer destination '{_destination.Name}' acknowledged committing {ack.Count} object(s) "
                    + $"of the {sent} this run sent it.");
            }

            // The acknowledgement carries the peer's signed statement of what
            // it committed and what it now holds (ADR-0064), and the run is
            // the moment that statement is worth most — the capture has just
            // reached the peer, and a source cannot cheaply list a peer's
            // replica to learn the same thing later. It goes through the very
            // seam the sync pass verifies at, never a second construction:
            // the same checks or none.
            string[] sentKeys;
            lock (_inventory)
            {
                sentKeys = [.. _sentKeys];
            }

            var (receipt, problem) = ReplicationInitiator.VerifyReplicationReceipt(
                ack,
                new ReplicationInitiator.ReceiptExpectation(
                    _session.Peer.Identity, _session.Binding, _keypair.Identity.PublicKey.ToArray()),
                _repositoryId,
                new HashSet<string>(sentKeys, StringComparer.Ordinal),
                _heldAtStart);

            return new CompletedShipment((long)ack.Count, receipt, problem);
        }
        catch (OperationCanceledException)
        {
            _faulted = true;
            throw;
        }
        catch (Exception exception)
        {
            _faulted = true;
            throw exception as IOException ?? Fault(_destination.Name, exception);
        }
        finally
        {
            _wire.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        // The opening inventory is a complete account of what the replica held,
        // and everything added since is this session's own doing — so a key in
        // neither is absent, answered without a round trip. A peer with a fresh
        // replica therefore costs no retrieval session at all, which is what
        // every seeding probe of a new destination asks.
        if (!Holds(key.Value))
        {
            return GetMetadataResult.NotFound;
        }

        var reads = await ReadsAsync(cancellationToken).ConfigureAwait(false);
        return reads is null
            ? GetMetadataResult.NotFound
            : await reads.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        if (!Holds(key.Value))
        {
            return OpenReadResult.NotFound;
        }

        var reads = await ReadsAsync(cancellationToken).ConfigureAwait(false);
        return reads is null
            ? OpenReadResult.NotFound
            : await reads.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Through retrieval rather than from the inventory, because a listing
        // entry carries a length and an inventory page does not (03 §3.3). A
        // listing of keys whose lengths were all reported as zero would be read
        // by the copier and the sampler as a replica full of empty objects,
        // which is worse than answering nothing.
        var reads = await ReadsAsync(cancellationToken).ConfigureAwait(false);
        if (reads is null)
        {
            yield break;
        }

        await foreach (var entry in reads.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Deletes nothing: a replica shrinks when its owner instructs it to, over
    /// the retention exchange (peer-protocol 06), never as a side effect of a
    /// store call.
    /// </summary>
    /// <remarks>
    /// Answering "not found" rather than throwing is the load-bearing part. The
    /// sink's sweep deletes through every destination in turn and a throw would
    /// take the sweep down; the truthful answer is that this object is still at
    /// the peer, which leaves it listed, re-condemned on the next pass, and
    /// carried out by the retention instruction that is entitled to do it.
    /// </remarks>
    /// <inheritdoc/>
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DeleteResult(DeleteOutcome.NotFound));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_retrieval is not null)
        {
            await _retrieval.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _keypair.Dispose();
        _wire.Dispose();
        _readGate.Dispose();
    }

    private bool Holds(string key)
    {
        lock (_inventory)
        {
            return _held.Contains(key);
        }
    }

    private void ThrowIfUnusable()
    {
        if (_faulted)
        {
            throw new IOException(
                $"Peer destination '{_destination.Name}' failed earlier in this run; its session is no longer usable.");
        }

        if (_closed)
        {
            throw new IOException(
                $"Peer destination '{_destination.Name}' has already been told this run is complete.");
        }
    }

    /// <summary>
    /// Streams one object: the header, then chunks at increasing offsets.
    /// </summary>
    /// <remarks>
    /// The header promises a length before the first chunk, so the length has
    /// to be known before any byte is written. A seekable stream says so
    /// itself — which is every stream this store is handed, since the sealed
    /// blob opens its spool file and metadata comes out of memory — and
    /// anything else is spooled to disk first rather than into memory, because
    /// an object may be half a gigabyte (NFR-PERF-001).
    /// </remarks>
    private async Task SendAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
    {
        string? spooled = null;
        var source = content;
        try
        {
            if (!content.CanSeek)
            {
                spooled = Path.Combine(
                    _runtime.Options.StateDirectory, "spool", "ship",
                    Convert.ToHexStringLower(_repositoryId.Span), Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(Path.GetDirectoryName(spooled)!);
                var file = new FileStream(
                    spooled, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 64 * 1024, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                source = new FileStream(
                    spooled, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, useAsync: true);
            }

            var length = source.Length - source.Position;
            await PeerFrame.WriteAsync(
                _session.Stream, new ReplicationObject(key.Value, (ulong)length, 0), cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[ReplicationChunk.MaximumBytes];
            var offset = 0UL;
            int got;
            while ((got = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await PeerFrame.WriteAsync(
                    _session.Stream, new ReplicationChunk(offset, buffer.AsMemory(0, got)), cancellationToken)
                    .ConfigureAwait(false);
                offset += (ulong)got;
            }

            if (offset != (ulong)length)
            {
                // The destination is waiting for bytes that are not coming, so
                // the session is finished either way; saying which object and
                // by how much is the difference between a diagnosable fault and
                // a hang.
                throw new IOException(
                    $"Object {key.Value} was announced to peer destination '{_destination.Name}' as {length} "
                    + $"bytes and delivered {offset}.");
            }
        }
        finally
        {
            if (spooled is not null)
            {
                if (!ReferenceEquals(source, content))
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    File.Delete(spooled);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Scratch that will not delete is swept with the rest of
                    // the spool; it is not this object's failure.
                }
            }
        }
    }

    /// <summary>
    /// The read half, dialled the first time something asks for bytes and kept
    /// for the rest of the run.
    /// </summary>
    /// <remarks>
    /// A refusal is remembered rather than retried: a peer that does not serve
    /// retrieval will not start during one capture, and re-dialling per read
    /// would turn one missing feature into a connection storm.
    /// </remarks>
    private async ValueTask<PeerRetrievalObjectStore?> ReadsAsync(CancellationToken cancellationToken)
    {
        if (_reads is not null || _retrievalRefused)
        {
            return _reads;
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reads is not null || _retrievalRefused)
            {
                return _reads;
            }

            if (!_session.Supports(PeerSessionNegotiation.RetrievalFeature))
            {
                _retrievalRefused = true;
                return null;
            }

            _retrieval = await PeerRetrievalClient.DialAsync(
                _runtime, _destination, _repositoryId, cancellationToken).ConfigureAwait(false);
            _reads = new PeerRetrievalObjectStore(_retrieval);
            return _reads;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A read half that cannot be opened is not the write half's
            // failure: the capture keeps shipping, and the reads answer absent.
            _retrievalRefused = true;
            return null;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private static async Task<HashSet<string>> ReadInventoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var page = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.ReplicationInventory, ReplicationInventory.Read, cancellationToken)
                .ConfigureAwait(false);

            foreach (var key in page.Keys)
            {
                held.Add(key);
            }

            if (!page.More)
            {
                return held;
            }
        }
    }

    /// <summary>
    /// Every wire failure in the storage vocabulary the sink's drop rule reads.
    /// </summary>
    private static IOException Fault(string destinationName, Exception exception) =>
        new($"Peer destination '{destinationName}': {exception.Message}", exception);
}

/// <summary>
/// What closing a run's peer exchange yielded: the count the destination
/// acknowledged, the receipt it signed for it
/// ([ADR-0064](../../docs/adr/0064-replication-receipts.md)), and why a
/// receipt that arrived was not believed.
/// </summary>
/// <param name="Committed">How many objects the destination acknowledged committing.</param>
/// <param name="Receipt">The verified receipt, or null when the destination sent none.</param>
/// <param name="Problem">
/// Why a receipt that arrived was rejected; null both when one was accepted
/// and when none came, which are different facts the caller tells apart by
/// <paramref name="Receipt"/>.
/// </param>
internal sealed record CompletedShipment(
    long Committed, ReplicationInitiator.VerifiedReplicationReceipt? Receipt, string? Problem);
