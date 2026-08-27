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
    private sealed record Shipment(string Name, LocalFileSystemObjectStore Store, int Priority);

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
    private long _shippedThisRun;

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
    /// <param name="cancellationToken">Cancels the seeding.</param>
    /// <exception cref="IOException">No destination is reachable — there is nowhere to write a backup.</exception>
    public async ValueTask BeginRunAsync(
        BackupSetConfiguration set, ulong nowUnixMilliseconds, CancellationToken cancellationToken)
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

            if (destination.Kind != DestinationKind.LocalPath)
            {
                // The peer write adapter lands with ADR-0046's later slice; a
                // stated incapacity, never a silent skip (FR-DEST-005's rule).
                skipped.Add((destination.Name, DestinationSyncState.NotSupported,
                    "direct-ship serves local-path destinations; peer shipping follows (ADR-0046)"));
                continue;
            }

            if (destination.AddressDefect is { } defect)
            {
                skipped.Add((destination.Name, DestinationSyncState.Failed, defect));
                continue;
            }

            if (!Directory.Exists(destination.Path))
            {
                skipped.Add((destination.Name, DestinationSyncState.Unavailable,
                    $"destination path '{destination.Path}' does not exist"));
                continue;
            }

            var record = _runtime.DestinationSync.Find(set.Id, destination.Name);
            if (!neverCaptured && record?.BaselineCompletedAt is null)
            {
                // Catch-up's job, not this run's: an incremental would hand
                // this destination a snapshot without its closure.
                skipped.Add((destination.Name, DestinationSyncState.Unavailable,
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

            if (DestinationCapacity.FloorShortfall(
                    destination.Path!, AvailableBytesOn(destination.Path!)) is { } shortOfSpace)
            {
                // The same floor the fan-out keeps (FR-DEST-010): a backup
                // must never be the reason the machine that owns the volume
                // cannot function. Unavailable, not failed — space freeing up
                // is the gap closing itself.
                skipped.Add((destination.Name, DestinationSyncState.Unavailable, shortOfSpace));
                continue;
            }

            // The store's own construction can refuse — a file squatting on
            // the replica root, a permission lost since the probe — and that
            // is this destination's drop, never the run's failure.
            try
            {
                inScope.Add(new Shipment(
                    destination.Name,
                    ReplicaStoreFor(destination),
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
                await SeedDescriptorAndKeysAsync(shipment, cancellationToken).ConfigureAwait(false);
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
        }
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
    public void CompleteRun(ulong nowUnixMilliseconds, bool succeeded = true)
    {
        List<Shipment> survivors;
        List<(string Name, string Error)> dropped;
        List<(string Name, DestinationSyncState State, string Error)> skipped;
        long shipped;
        lock (_gate)
        {
            survivors = [.. _inScope];
            dropped = [.. _droppedThisRun.Select(pair => (pair.Key, pair.Value))];
            skipped = [.. _skippedThisRun];
            shipped = _shippedThisRun;
            _inScope = [];
            _runActive = false;
            _droppedThisRun.Clear();
            _skippedThisRun.Clear();
            _shippedThisRun = 0;
        }

        if (succeeded)
        {
            foreach (var survivor in survivors)
            {
                _runtime.DestinationSync.RecordSuccess(_setId, survivor.Name, shipped, nowUnixMilliseconds);
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
        // Retention against direct-ship destinations is per-destination
        // convergence (ADR-0046's next slice); nothing on the capture path
        // deletes, and a blanket delete through the sink would be one policy
        // pretending to be every destination's.
        var result = IsBlobKey(key.Value)
            ? new DeleteResult(DeleteOutcome.NotFound)
            : await _metadata.DeleteAsync(key, conditions, cancellationToken).ConfigureAwait(false);
        return result;
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
        // service restart.
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
    /// Ensures a destination holds the repository's descriptor and keys —
    /// what makes its replica independently openable from its first byte.
    /// Cheap and idempotent: two-ish tiny objects, put if absent.
    /// </summary>
    private async ValueTask SeedDescriptorAndKeysAsync(Shipment target, CancellationToken cancellationToken)
    {
        await foreach (var entry in _metadata
            .ListAsync(ObjectPrefix.Parse("keys/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            await CopyIfAbsentAsync(target, entry.Key, cancellationToken).ConfigureAwait(false);
        }

        await CopyIfAbsentAsync(target, Repository.RepositoryLifecycle.DescriptorKey, cancellationToken)
            .ConfigureAwait(false);
    }

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

    private LocalFileSystemObjectStore ReplicaStoreFor(DestinationConfiguration destination) =>
        new(Path.Combine(destination.Path!, _repositoryIdHex), _log);

    private static long? AvailableBytesOn(string destinationRoot)
    {
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
