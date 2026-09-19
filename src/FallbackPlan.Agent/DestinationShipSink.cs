using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// The direct-ship store (ADR-0046): one <see cref="IObjectStore"/> the
/// publication pipeline writes as if it were a staging archive, that in fact
/// fans every object out to the set's destinations and keeps only metadata
/// on the agent's machine.
/// </summary>
/// <remarks>
/// <para>
/// Routing, by key: <c>blobs/</c> objects go to the in-scope destinations
/// and NEVER to the local metadata store; everything else — descriptor,
/// keys, journal, index, snapshots, hints — goes to the local metadata store
/// AND the in-scope destinations, so each destination is a whole,
/// independently restorable repository and the agent still holds the small
/// planning copy its diffs and listings read.
/// </para>
/// <para>
/// Reads route the other way: metadata is answered locally; a <c>blobs/</c>
/// read — the dedupe presence probe, a copier's fetch — is answered by the
/// first destination that holds the key, in priority order; a <c>blobs/</c>
/// listing is the union across destinations, because every committed
/// snapshot's closure exists at at least one destination by construction (a
/// capture refuses to run with none reachable, so nothing commits nowhere).
/// This is also what lets the existing fan-out act as the catch-up pump for
/// a destination that missed a run: copying "from the archive" through this
/// sink copies from whichever sibling holds the bytes.
/// </para>
/// <para>
/// A destination that fails mid-run is dropped from the run and recorded in
/// the sync ledger; the run continues while at least one destination
/// remains, and the dropped replica is simply lagging-but-valid — its
/// journal holds an intent nothing retired, exactly the state an
/// interrupted copy leaves, healed by the next catch-up. When the LAST
/// destination fails, the put faults and the backup fails through the
/// pipeline's ordinary interruption safety.
/// </para>
/// </remarks>
public sealed class DestinationShipSink : IObjectStore
{
    private sealed record Shipment(string Name, IObjectStore Store, int Priority);

    private readonly ServiceRuntime _runtime;
    private readonly LocalFileSystemObjectStore _metadata;
    private readonly string _setId;
    private readonly string _repositoryIdHex;
    private readonly ILogger _log;
    private readonly LocalFileSystemObjectStore? _stagingFallback;
    private readonly Lock _gate = new();
    private List<Shipment> _inScope = [];
    private bool _runActive;
    private readonly Dictionary<string, string> _droppedThisRun = new(StringComparer.Ordinal);
    private readonly List<(string Name, DestinationSyncState State, string Error)> _skippedThisRun = [];

    // Every peer session this run opened, survivors and casualties alike. A
    // dropped destination leaves _inScope at the moment it fails, so this is
    // the only list that can still close and dispose its session — and a live
    // TLS connection nobody closes is a socket the peer holds open until it
    // times out (ADR-0058).
    private readonly List<PeerShipStore> _peersThisRun = [];
    private long _shippedThisRun;

    // The highest publication sequence this run put on the wire, read out of
    // the snapshot record's own cleartext prefix as it goes past (ADR-0056).
    // A direct-ship run IS the fan-out, so nothing else is in a position to
    // record what the destinations now hold: before this, the ledger's
    // synced_sequence sat at whatever the last copy pass wrote and a set that
    // never needed a copy pass left it at its first value for ever.
    private ulong _publishedThisRun;

    internal DestinationShipSink(
        ServiceRuntime runtime,
        LocalFileSystemObjectStore metadata,
        string setId,
        string repositoryIdHex,
        ILogger? log = null,
        LocalFileSystemObjectStore? stagingFallback = null)
    {
        _runtime = runtime;
        _metadata = metadata;
        _setId = setId;
        _repositoryIdHex = repositoryIdHex;
        _log = log ?? NullLogger.Instance;

        // A migrated set's not-yet-retired staging archive (ADR-0046): a
        // read-only seed source consulted LAST — history a destination does
        // not hold yet answers from here, and the catch-up copy through this
        // sink is what carries it outward. Never written; retirement deletes
        // it, after which its reads simply answer not-found.
        _stagingFallback = stagingFallback;
    }

    /// <inheritdoc />
    public StoreCapabilities Capabilities => _metadata.Capabilities;

    /// <summary>
    /// Resolves this run's write targets and seeds each with the repository's
    /// descriptor and keys. In scope: the set's defect-free local-path
    /// destinations whose directory exists and that hold a baseline — or all
    /// reachable ones when the set has never captured, because that first
    /// capture ships everything and IS every destination's full backup
    /// (ADR-0047's needs-full rule; a baseline-less destination on a set
    /// with history is caught up from a sibling instead, since an
    /// incremental would hand it a snapshot without its closure).
    /// </summary>
    /// <param name="set">The set as configured for this run.</param>
    /// <param name="nowUnixMilliseconds">The clock, for the ledger rows.</param>
    /// <param name="reclaimPublicKey">
    /// The repository's reclaim public key (ADR-0055 §5), published on the
    /// offer a peer destination is opened with and recorded by it at first
    /// attribution; empty for a local path, which needs none, and for a
    /// repository that publishes none.
    /// </param>
    /// <param name="claimPublicKey">
    /// The installation's claim public key (ADR-0053 §1), published on the
    /// same offer and recorded by the same attribution; empty for a local
    /// path, and for an installation that publishes none.
    /// </param>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <exception cref="IOException">No destination is reachable — there is nowhere to write a backup.</exception>
    public async ValueTask BeginRunAsync(
        BackupSetConfiguration set,
        ulong nowUnixMilliseconds,
        ReadOnlyMemory<byte> reclaimPublicKey,
        ReadOnlyMemory<byte> claimPublicKey,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(set);

        var neverCaptured = !await AnySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var configuration = _runtime.Configuration;
        var lastCompleted = _runtime.Jobs.LastCompleted(set.Id)?.UpdatedAt ?? 0;
        var inScope = new List<Shipment>();
        var skipped = new List<(string, DestinationSyncState, string)>();
        var dropped = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reference in set.Destinations)
        {
            if (configuration.FindDestination(reference.Ref) is not { } destination)
            {
                skipped.Add((reference.Ref, DestinationSyncState.Failed,
                    $"destination '{reference.Ref}' is no longer declared"));
                continue;
            }

            if (destination.Kind is not (DestinationKind.LocalPath or DestinationKind.Peer))
            {
                // The reserved cloud kinds (FR-DEST-005): configuration models
                // them, the runtime does not serve them yet. A stated
                // incapacity, never a silent skip.
                skipped.Add((destination.Name, DestinationSyncState.NotSupported,
                    $"destination kind '{destination.Kind}' is not yet supported"));
                continue;
            }

            if (destination.AddressDefect is { } defect)
            {
                skipped.Add((destination.Name, DestinationSyncState.Failed, defect));
                continue;
            }

            if (destination.Kind == DestinationKind.LocalPath && !Directory.Exists(destination.Path))
            {
                skipped.Add((destination.Name, DestinationSyncState.Unavailable,
                    $"destination path '{destination.Path}' does not exist"));
                continue;
            }

            var record = _runtime.DestinationSync.Find(set.Id, destination.Name);
            if (!neverCaptured && record?.BaselineCompletedAt is null)
            {
                // Catch-up's job, not this run's: an incremental would hand
                // this destination a snapshot without its closure. Recorded
                // as BEHIND, never as a counted failure — the destination
                // did nothing wrong, and the distinction is load-bearing:
                // the pass runs captures before its fan-out phase, so a
                // failure stamped here would push the seeding sync behind
                // its own back-off on every due capture. On a schedule
                // whose period is at or under the back-off cap that seed
                // would never run at all; a behind row syncs at once.
                skipped.Add((destination.Name, DestinationSyncState.Behind,
                    "this destination holds no full backup yet; it is seeded from a sibling replica"));
                continue;
            }

            if (!neverCaptured
                && _stagingFallback is null
                && (record!.LastSuccessAt is null || record.LastSuccessAt < lastCompleted))
            {
                // A destination that missed a run holds an incomplete
                // history, and this run's dedupe probe is satisfied by ANY
                // holder — including it would write snapshot metadata whose
                // blob closure never ships, a replica that is not
                // independently restorable while the ledger says it is.
                // Catch-up brings it current; the next run re-admits it.
                // A MIGRATING set is the stated exception (ADR-0046's
                // migration record): while the staging archive remains as
                // the read-only seed source, per-destination completeness is
                // deliberately the union's promise, the pass always syncs
                // the pair, and retire_staging is what certifies the
                // destinations before staging leaves.
                skipped.Add((destination.Name, DestinationSyncState.Behind,
                    "this destination missed a run and holds an incomplete history; catch-up brings it current first"));
                continue;
            }

            if (destination.Kind == DestinationKind.LocalPath
                && DestinationCapacity.FloorShortfall(
                    destination.Path!, AvailableBytesOn(destination.Path!)) is { } shortOfSpace)
            {
                // The same floor the fan-out keeps (FR-DEST-010): a backup
                // must never be the reason the machine that owns the volume
                // cannot function. Unavailable, not failed — space freeing up
                // is the gap closing itself.
                skipped.Add((destination.Name, DestinationSyncState.Unavailable, shortOfSpace));
                continue;
            }

            // Opening the destination can refuse — a file squatting on the
            // replica root, a permission lost since the probe, a peer that is
            // not answering — and that is this destination's drop, never the
            // run's failure. A peer's refusal arrives as an IOException by
            // construction, so both kinds are dropped by one rule (ADR-0058).
            try
            {
                inScope.Add(new Shipment(
                    destination.Name,
                    await StoreForAsync(destination, reclaimPublicKey, claimPublicKey, cancellationToken)
                        .ConfigureAwait(false),
                    SetDestinationReference.EffectivePriority(reference, destination)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.ShipDestinationDropped(_log, destination.Name, exception.Message);
                dropped[destination.Name] = exception.Message;
            }
        }

        // Seeding is per destination under the same drop rule as every later
        // put: one unwritable destination is dropped and named, never the
        // reason a capture with a healthy sibling refuses (ADR-0046 §3).
        var seeded = new List<Shipment>();
        foreach (var shipment in inScope.OrderByDescending(candidate => candidate.Priority))
        {
            try
            {
                await SeedDescriptorAsync(shipment, cancellationToken).ConfigureAwait(false);
                seeded.Add(shipment);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.ShipDestinationDropped(_log, shipment.Name, exception.Message);
                dropped[shipment.Name] = exception.Message;
            }
        }

        if (seeded.Count == 0)
        {
            foreach (var (name, error) in dropped)
            {
                _runtime.DestinationSync.RecordFailure(
                    set.Id, name, DestinationSyncState.Failed, error, nowUnixMilliseconds);
            }

            foreach (var (name, state, error) in skipped)
            {
                RecordSkip(set.Id, name, state, error, nowUnixMilliseconds);
            }

            throw new IOException(
                $"Backup set '{set.Name}' has no reachable destination to write to — with no staging archive "
                + "(ADR-0046), a capture with nowhere to ship has nothing it can promise. "
                + "Reconnect a destination and run again.");
        }

        lock (_gate)
        {
            _inScope = seeded;
            _runActive = true;
            _droppedThisRun.Clear();
            foreach (var (name, error) in dropped)
            {
                _droppedThisRun[name] = error;
            }

            _skippedThisRun.Clear();
            _skippedThisRun.AddRange(skipped);
            _shippedThisRun = 0;
            _publishedThisRun = 0;
        }
    }

    /// <summary>
    /// Files the receipt a run's peer signed for what it committed, and
    /// counts the pair complete on its strength
    /// ([ADR-0064](../../docs/adr/0064-replication-receipts.md)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source cannot cheaply list a peer's replica, so the acknowledgement
    /// that closes the run is the only measurement this shipment will get —
    /// and it is worth most now, the capture having just reached the peer.
    /// Before this the run read the acknowledgement's count and dropped the
    /// statement behind it, leaving the pair uncounted until a sync pass over
    /// the same peer happened to fall due.
    /// </para>
    /// <para>
    /// A rejected receipt is a notice and never a refusal: the objects are at
    /// the peer, acknowledged and counted against what was sent, and what is
    /// missing is a statement of it that holds up. The run's own success is
    /// untouched; what the pair does not get is a completeness figure, because
    /// the only thing that would have supported one is the receipt.
    /// </para>
    /// </remarks>
    /// <param name="destinationName">The peer destination, for the ledger and the notice.</param>
    /// <param name="completed">What closing the exchange yielded.</param>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    private void RecordShipmentReceipt(
        string destinationName, CompletedShipment completed, ulong nowUnixMilliseconds)
    {
        var set = _runtime.Configuration.BackupSets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _setId, StringComparison.Ordinal));
        var setName = set?.Name ?? _setId;

        if (completed.Problem is { } problem)
        {
            Log.ReplicationReceiptRejected(_log, destinationName, setName, problem);
            _runtime.Notices.Raise(
                $"replication-receipt-invalid:{_setId}:{destinationName}",
                $"peer '{destinationName}' acknowledged set '{setName}'s capture with a receipt this installation "
                + $"will not file: {problem}. The peer acknowledged committing {completed.Committed} object(s) and "
                + "that stands; what is missing is a statement of what it holds under its own signature that "
                + "holds up, so this peer is not counted complete on this run. A peer that misattests once "
                + "deserves a look.",
                nowUnixMilliseconds);
            return;
        }

        if (completed.Receipt is not { } receipt)
        {
            return;
        }

        try
        {
            _ = Protocol.ReplicationReceiptStore.Open(_runtime.Options.StateDirectory).File(
                Protocol.DeletionReceiptRole.Commander, receipt.SignedBytes.Span, receipt.Signature.Span,
                receipt.Signer, setName, destinationName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.ReplicationReceiptNotFiledByCommander(_log, destinationName, setName, exception.Message);
        }

        // Verified is what counts, filed or not: the attestation was made and
        // checked, and a copy this side could not keep changes nothing about
        // what the peer holds.
        //
        // Held and owed are both the peer's own attested figure, because a
        // direct-ship run has no other: it ships as it captures and never
        // computes what the destination is owed the way a sync pass does. The
        // pair therefore reads complete, which it is — everything this run
        // sent was acknowledged and signed for. A later sync pass over the
        // same pair overwrites both with its own arithmetic; the two agree on
        // completeness and may differ slightly in magnitude, which is the
        // honest consequence of measuring the same thing two ways.
        var attested = (long)Math.Min(receipt.Receipt.HeldBytes, long.MaxValue);
        _runtime.DestinationSync.RecordCompleteness(
            _setId, destinationName, attested, attested, nowUnixMilliseconds);
    }

    /// <summary>
    /// Closes the run's books, whatever ended it: on success a ledger row
    /// (and, first time, the baseline) for every destination that stayed in
    /// scope; on ANY ending, the named failure for every destination dropped
    /// or skipped — a run that failed still owes the ledger its drops, or no
    /// back-off arms and the healing catch-up never schedules. Also releases
    /// the run's read scope: outside a run, reads resolve fresh from the
    /// configuration, so a destination plugged back in answers without a
    /// service restart.
    /// </summary>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    /// <param name="succeeded">Whether the run committed its snapshot.</param>
    public async ValueTask CompleteRunAsync(ulong nowUnixMilliseconds, bool succeeded = true)
    {
        List<Shipment> survivors;
        List<(string Name, string Error)> dropped;
        List<(string Name, DestinationSyncState State, string Error)> skipped;
        List<PeerShipStore> peers;
        long shipped;
        ulong published;
        lock (_gate)
        {
            survivors = [.. _inScope];
            dropped = [.. _droppedThisRun.Select(pair => (pair.Key, pair.Value))];
            skipped = [.. _skippedThisRun];
            peers = [.. _peersThisRun];
            shipped = _shippedThisRun;
            published = _publishedThisRun;
            _inScope = [];
            _runActive = false;
            _droppedThisRun.Clear();
            _skippedThisRun.Clear();
            _peersThisRun.Clear();
            _shippedThisRun = 0;
            _publishedThisRun = 0;
        }

        // A peer's exchange is not finished until it has been told so and has
        // said how much it committed, and that count is the only evidence this
        // run has that the replica holds what the ledger is about to claim
        // (ADR-0058). A peer that will not close is recorded as failed, however
        // the run itself ended — and its session is disposed either way.
        // The books close however the run ended, so the closing exchange cannot
        // be held to the run's own cancellation — but nor may a peer that has
        // stopped answering hold a service shutdown open.
        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var peer in peers)
        {
            try
            {
                if (succeeded)
                {
                    var completed = await peer.CompleteAsync(closing.Token).ConfigureAwait(false);
                    RecordShipmentReceipt(peer.DestinationName, completed, nowUnixMilliseconds);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Log.ShipDestinationDropped(_log, peer.DestinationName, exception.Message);
                survivors.RemoveAll(candidate =>
                    string.Equals(candidate.Name, peer.DestinationName, StringComparison.Ordinal));
                if (!dropped.Exists(entry => string.Equals(entry.Name, peer.DestinationName, StringComparison.Ordinal)))
                {
                    dropped.Add((peer.DestinationName, exception.Message));
                }
            }
            finally
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (succeeded)
        {
            foreach (var survivor in survivors)
            {
                // A survivor received every object of this run, this run's
                // snapshot included, so it holds everything published at or
                // before that sequence — which is what the ledger's watermark
                // means and what lets the next pass answer without listing
                // (ADR-0056, FR-GC-009).
                _runtime.DestinationSync.RecordSuccess(
                    _setId, survivor.Name, shipped, nowUnixMilliseconds, published);
            }
        }

        foreach (var (name, error) in dropped)
        {
            _runtime.DestinationSync.RecordFailure(
                _setId, name, DestinationSyncState.Failed, error, nowUnixMilliseconds);
        }

        foreach (var (name, state, error) in skipped)
        {
            RecordSkip(_setId, name, state, error, nowUnixMilliseconds);
        }
    }

    /// <summary>
    /// A skip is not always a failure: a behind destination is deliberately
    /// held out for catch-up, and counting that against it would start a
    /// back-off exactly where an immediate heal is wanted.
    /// </summary>
    private void RecordSkip(
        string setId, string name, DestinationSyncState state, string error, ulong nowUnixMilliseconds)
    {
        if (state == DestinationSyncState.Behind)
        {
            _runtime.DestinationSync.RecordBehind(setId, name, error, nowUnixMilliseconds);
            return;
        }

        _runtime.DestinationSync.RecordFailure(setId, name, state, error, nowUnixMilliseconds);
    }

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        var isBlob = IsBlobKey(key.Value);
        PutResult? metadataResult = null;

        if (!isBlob)
        {
            // The planning copy first: a metadata object the agent cannot
            // read back is a diff it cannot plan.
            metadataResult = await _metadata.PutAsync(key, openContent, conditions, cancellationToken)
                .ConfigureAwait(false);

            await NoteIfSnapshotAsync(key, cancellationToken).ConfigureAwait(false);
        }

        List<Shipment> targets;
        bool runActive;
        lock (_gate)
        {
            targets = [.. _inScope];
            runActive = _runActive;
        }

        if (!runActive)
        {
            // Outside a run — migration, a seeding copy, an operator verb —
            // targets resolve fresh from the configuration, exactly as reads
            // do, so a destination plugged back in receives without a
            // service restart.
            targets = ReadOrder();
        }

        if (targets.Count == 0)
        {
            if (metadataResult is { } local)
            {
                // A metadata write outside a run has a home even with every
                // destination away: the planning copy. The catch-up carries
                // it outward when one returns.
                return local;
            }

            // Mid-run this is every destination having failed (the last
            // failure already threw); outside a run it is a blob write with
            // nowhere at all to land.
            throw new IOException(runActive
                ? $"Every destination of set '{_setId}' failed mid-run; nothing remains to write to."
                : $"Set '{_setId}' has no reachable destination to write '{key.Value}' to.");
        }

        // The first copy lands at the highest-priority destination before the
        // rest ship concurrently (ADR-0047's ordering promise).
        var outcomes = new List<PutOutcome> { await ShipAsync(targets[0], key, openContent, conditions, cancellationToken).ConfigureAwait(false) };
        if (targets.Count > 1)
        {
            outcomes.AddRange(await Task.WhenAll(targets.Skip(1).Select(target =>
                ShipAsync(target, key, openContent, conditions, cancellationToken).AsTask())).ConfigureAwait(false));
        }

        var landed = outcomes.Where(outcome => outcome is not PutOutcome.PreconditionFailed).ToList();
        if (isBlob && landed.Count > 0)
        {
            Interlocked.Increment(ref _shippedThisRun);
        }

        // Created if anyone created; AlreadyExists only when every
        // destination already held it — that is when the byte-identity
        // readback upstream has something real to compare against.
        return landed.Count == 0
            ? new PutResult(PutOutcome.PreconditionFailed)
            : new PutResult(landed.Any(outcome => outcome == PutOutcome.Created)
                ? PutOutcome.Created
                : PutOutcome.AlreadyExists);
    }

    /// <inheritdoc />
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        if (!IsBlobKey(key.Value))
        {
            return await _metadata.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        }

        foreach (var holder in ReadOrder())
        {
            var result = await holder.Store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (result.Found)
            {
                return result;
            }
        }

        if (_stagingFallback is not null)
        {
            var fallback = await _stagingFallback.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (fallback.Found)
            {
                return fallback;
            }
        }

        return GetMetadataResult.NotFound;
    }

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        if (!IsBlobKey(key.Value))
        {
            return await _metadata.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        }

        foreach (var holder in ReadOrder())
        {
            var result = await holder.Store.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != OpenReadOutcome.NotFound)
            {
                return result;
            }
        }

        if (_stagingFallback is not null)
        {
            var fallback = await _stagingFallback.OpenReadAsync(key, range, cancellationToken)
                .ConfigureAwait(false);
            if (fallback.Outcome != OpenReadOutcome.NotFound)
            {
                return fallback;
            }
        }

        return OpenReadResult.NotFound;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A prefix inside blobs/ can match no metadata key; anything shorter
        // — including one that merely shares letters with "blobs/", like "b"
        // — can match both planes, and the planning copy must answer even
        // with every destination away.
        var value = prefix.Value;
        if (!IsBlobKey(value))
        {
            await foreach (var entry in _metadata.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            if (value.Length > 0 && !"blobs/".StartsWith(value, StringComparison.Ordinal))
            {
                yield break;
            }
        }

        // The union across destinations: a behind sibling lacks what it
        // missed, and whoever holds a key answers for it. Every committed
        // snapshot's closure exists somewhere in this union by construction.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blobPrefix = value.Length == 0 ? ObjectPrefix.Parse("blobs/") : prefix;
        foreach (var holder in ReadOrder())
        {
            await foreach (var entry in holder.Store.ListAsync(blobPrefix, options, cancellationToken)
                .ConfigureAwait(false))
            {
                if (seen.Add(entry.Key.Value))
                {
                    yield return entry;
                }
            }
        }

        if (_stagingFallback is not null)
        {
            // History awaiting retirement is part of the union — this is
            // what makes the catch-up copy carry it to the destinations.
            await foreach (var entry in _stagingFallback.ListAsync(blobPrefix, options, cancellationToken)
                .ConfigureAwait(false))
            {
                if (seen.Add(entry.Key.Value))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        // A delete reaching the sink is already per-destination-policy-safe:
        // the replication gate holds a snapshot's expiry until every entitled
        // destination's own keep-set has dropped it (FR-GC-010), so what the
        // sweep condemns is condemned for every holder. It used to stop at
        // the metadata store — "convergence's job" — but nothing converged:
        // destinations never shrank under retention, and the union listing
        // resurrected every swept object into the next survey, condemned
        // again for ever (the ADR-0046 trimming drill's finding).
        //
        // Destinations go first, the metadata store last: interrupted midway,
        // the metadata plane still lists the object, the next pass
        // re-condemns, and the delete converges. The reverse order is the
        // stranding this replaces. An unreachable destination keeps its copy
        // for now — re-listed through the union, it is re-condemned on the
        // next pass once the destination returns. The staging fallback is
        // never touched: it is a read-only seed, and retire_staging is its
        // one deleter (ADR-0046).
        var deleted = false;
        foreach (var shipment in ReadOrder())
        {
            var outcome = await shipment.Store.DeleteAsync(key, conditions, cancellationToken)
                .ConfigureAwait(false);
            deleted |= outcome.Outcome == DeleteOutcome.Deleted;
        }

        if (!IsBlobKey(key.Value))
        {
            var metadata = await _metadata.DeleteAsync(key, conditions, cancellationToken).ConfigureAwait(false);
            deleted |= metadata.Outcome == DeleteOutcome.Deleted;
        }

        return new DeleteResult(deleted ? DeleteOutcome.Deleted : DeleteOutcome.NotFound);
    }

    /// <summary>The destinations a read may consult: every configured, reachable local path, priority first.</summary>
    private List<Shipment> ReadOrder()
    {
        lock (_gate)
        {
            if (_inScope.Count > 0)
            {
                return [.. _inScope];
            }
        }

        // Outside a run — a catch-up copy, a preview — resolve fresh from the
        // configuration, so a destination plugged back in answers without a
        // service restart. Local paths only: a peer shipment is a live session
        // rather than a directory, and dialling one per read outside a run
        // would put a TLS handshake behind a presence probe. A peer's replica
        // is read back on the restore-source path instead (ADR-0041), and the
        // fan-out's own push reads through this sink's union, which for a
        // peer-only set is the peer's session while the run holds it open.
        var configuration = _runtime.Configuration;
        var set = configuration.BackupSets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _setId, StringComparison.Ordinal));
        if (set is null)
        {
            return [];
        }

        var holders = new List<Shipment>();
        foreach (var reference in set.Destinations)
        {
            if (configuration.FindDestination(reference.Ref) is not
                { Kind: DestinationKind.LocalPath, AddressDefect: null } destination
                || !Directory.Exists(destination.Path))
            {
                continue;
            }

            try
            {
                holders.Add(new Shipment(
                    destination.Name,
                    ReplicaStoreFor(destination),
                    SetDestinationReference.EffectivePriority(reference, destination)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A replica root that cannot even be opened is a holder that
                // cannot answer; the next holder, or not-found, is the truth.
            }
        }

        return [.. holders.OrderByDescending(holder => holder.Priority)];
    }

    private async ValueTask<PutOutcome> ShipAsync(
        Shipment target,
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await target.Store.PutAsync(key, openContent, conditions, cancellationToken)
                .ConfigureAwait(false);
            return result.Outcome;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Dropped from the run, named in the ledger at CompleteRun; the
            // replica is lagging-but-valid and catch-up heals it. The run
            // itself continues while anyone remains — and if nobody does, the
            // rethrow below fails the backup through the pipeline's ordinary
            // interruption safety.
            bool anyLeft;
            lock (_gate)
            {
                _inScope.RemoveAll(candidate => string.Equals(candidate.Name, target.Name, StringComparison.Ordinal));
                _droppedThisRun[target.Name] = exception.Message;
                anyLeft = _inScope.Count > 0;
            }

            Log.ShipDestinationDropped(_log, target.Name, exception.Message);
            if (!anyLeft)
            {
                throw new IOException(
                    $"The last destination ('{target.Name}') failed: {exception.Message}", exception);
            }

            return PutOutcome.PreconditionFailed;
        }
    }

    /// <summary>
    /// Ensures a destination holds the repository's descriptor — what makes
    /// its replica independently openable from its first byte: the salt,
    /// the parameters and the sealing public key a passphrase re-derives
    /// against (ADR-0042 §1). Cheap and idempotent: one tiny object, put if
    /// absent.
    /// </summary>
    private async ValueTask SeedDescriptorAsync(Shipment target, CancellationToken cancellationToken) =>
        await CopyIfAbsentAsync(target, Repository.RepositoryLifecycle.DescriptorKey, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask CopyIfAbsentAsync(Shipment target, ObjectKey key, CancellationToken cancellationToken)
    {
        var held = await target.Store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (held.Found)
        {
            return;
        }

        _ = await target.Store.PutAsync(
            key,
            async token =>
            {
                var read = await _metadata.OpenReadAsync(key, range: null, token).ConfigureAwait(false);
                return read.Outcome == OpenReadOutcome.Found && read.Content is not null
                    ? read.Content
                    : throw new IOException($"Object {key.Value} is missing from the metadata store.");
            },
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This destination as a store to write through: a directory for a local
    /// path, a live replication push session for a peer (ADR-0058).
    /// </summary>
    /// <param name="destination">The destination's declaration.</param>
    /// <param name="reclaimPublicKey">The repository's reclaim public key, for a peer's offer.</param>
    /// <param name="claimPublicKey">The installation's claim public key, for the same offer.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    private async ValueTask<IObjectStore> StoreForAsync(
        DestinationConfiguration destination,
        ReadOnlyMemory<byte> reclaimPublicKey,
        ReadOnlyMemory<byte> claimPublicKey,
        CancellationToken cancellationToken)
    {
        if (destination.Kind != DestinationKind.Peer)
        {
            return ReplicaStoreFor(destination);
        }

        var peer = await PeerShipStore.OpenAsync(
            _runtime, destination, Convert.FromHexString(_repositoryIdHex), reclaimPublicKey, claimPublicKey,
            cancellationToken)
            .ConfigureAwait(false);

        // Registered the moment it exists, not when the run admits it: seeding
        // can still refuse this destination, and an unregistered session is one
        // nobody closes.
        lock (_gate)
        {
            _peersThisRun.Add(peer);
        }

        return peer;
    }

    private IObjectStore ReplicaStoreFor(DestinationConfiguration destination)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(destination.Path!, _repositoryIdHex), _log);
        return _runtime.Options.ReplicaStoreDecorator?.Invoke(destination.Name, store) ?? store;
    }

    /// <summary>
    /// Reads the publication sequence out of a snapshot object as it is
    /// written, and keeps the run's highest.
    /// </summary>
    /// <remarks>
    /// The number is in the record's cleartext prefix (specification 08 §2),
    /// so this needs no keys and no catalogue — the same reading the fan-out
    /// does when it surveys a staging archive, done once on the object that
    /// carries it rather than over every snapshot in the set. A snapshot that
    /// will not parse claims nothing: the run then records the sequence it
    /// already had, which understates what the destination holds and is the
    /// safe direction to be wrong in.
    /// </remarks>
    /// <param name="key">The object just written.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    private async ValueTask NoteIfSnapshotAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        if (!key.Value.StartsWith("snapshots/", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var read = await _metadata.OpenReadAsync(key, range: null, cancellationToken)
                .ConfigureAwait(false);
            if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
            {
                return;
            }

            using var memory = new MemoryStream();
            await read.Content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var counter = Repository.Format.Records.StandaloneRecordFraming.Parse(memory.ToArray()).Counter;

            lock (_gate)
            {
                _publishedThisRun = Math.Max(_publishedThisRun, counter);
            }
        }
        catch (Exception exception) when (exception is FormatException or IOException)
        {
            // The watermark is an optimisation over a listing, never a
            // correctness input: not knowing it costs a pass that reads
            // through, which is what every pass did before it existed.
        }
    }

    private long? AvailableBytesOn(string destinationRoot)
    {
        if (_runtime.Options.AvailableBytesProbe is { } probe)
        {
            return probe(destinationRoot);
        }

        try
        {
            return new DriveInfo(DestinationCapacity.ProbeRootFor(destinationRoot)).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A platform that will not say answers "there is room": the floor
            // exists to stop a disk being filled, never to stop a healthy
            // destination receiving backups (FR-DEST-010).
            return null;
        }
    }

    private async ValueTask<bool> AnySnapshotAsync(CancellationToken cancellationToken)
    {
        await foreach (var _ in _metadata
            .ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private static bool IsBlobKey(string key) => key.StartsWith("blobs/", StringComparison.Ordinal);
}
