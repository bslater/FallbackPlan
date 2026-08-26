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
    private readonly Lock _gate = new();
    private List<Shipment> _inScope = [];
    private readonly Dictionary<string, string> _droppedThisRun = new(StringComparer.Ordinal);
    private readonly List<(string Name, DestinationSyncState State, string Error)> _skippedThisRun = [];
    private long _shippedThisRun;

    internal DestinationShipSink(
        ServiceRuntime runtime,
        LocalFileSystemObjectStore metadata,
        string setId,
        string repositoryIdHex,
        ILogger? log = null)
    {
        _runtime = runtime;
        _metadata = metadata;
        _setId = setId;
        _repositoryIdHex = repositoryIdHex;
        _log = log ?? NullLogger.Instance;
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
        var inScope = new List<Shipment>();
        var skipped = new List<(string, DestinationSyncState, string)>();

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

            var baseline = _runtime.DestinationSync.Find(set.Id, destination.Name)?.BaselineCompletedAt;
            if (!neverCaptured && baseline is null)
            {
                // Catch-up's job, not this run's: an incremental would hand
                // this destination a snapshot without its closure.
                skipped.Add((destination.Name, DestinationSyncState.Unavailable,
                    "this destination holds no full backup yet; it is seeded from a sibling replica"));
                continue;
            }

            inScope.Add(new Shipment(
                destination.Name,
                ReplicaStoreFor(destination),
                reference.Priority ?? destination.Priority ?? 0));
        }

        if (inScope.Count == 0)
        {
            foreach (var (name, state, error) in skipped)
            {
                _runtime.DestinationSync.RecordFailure(set.Id, name, state, error, nowUnixMilliseconds);
            }

            throw new IOException(
                $"Backup set '{set.Name}' has no reachable destination to write to — with no staging archive "
                + "(ADR-0046), a capture with nowhere to ship has nothing it can promise. "
                + "Reconnect a destination and run again.");
        }

        foreach (var shipment in inScope.OrderByDescending(candidate => candidate.Priority))
        {
            await SeedDescriptorAndKeysAsync(shipment, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _inScope = [.. inScope.OrderByDescending(candidate => candidate.Priority)];
            _droppedThisRun.Clear();
            _skippedThisRun.Clear();
            _skippedThisRun.AddRange(skipped);
            _shippedThisRun = 0;
        }
    }

    /// <summary>
    /// Records this run's per-destination outcomes in the sync ledger: a
    /// success (and, first time, the baseline) for every destination that
    /// stayed in scope, the named failure for every one that was dropped or
    /// skipped.
    /// </summary>
    /// <param name="nowUnixMilliseconds">The clock.</param>
    public void CompleteRun(ulong nowUnixMilliseconds)
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
        }

        foreach (var survivor in survivors)
        {
            _runtime.DestinationSync.RecordSuccess(_setId, survivor.Name, shipped, nowUnixMilliseconds);
        }

        foreach (var (name, error) in dropped)
        {
            _runtime.DestinationSync.RecordFailure(
                _setId, name, DestinationSyncState.Failed, error, nowUnixMilliseconds);
        }

        foreach (var (name, state, error) in skipped)
        {
            _runtime.DestinationSync.RecordFailure(_setId, name, state, error, nowUnixMilliseconds);
        }
    }

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        var isBlob = IsBlobKey(key.Value);

        if (!isBlob)
        {
            // The planning copy first: a metadata object the agent cannot
            // read back is a diff it cannot plan.
            _ = await _metadata.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        }

        List<Shipment> targets;
        lock (_gate)
        {
            targets = [.. _inScope];
        }

        if (targets.Count == 0)
        {
            // Writes happen only inside a run; a run begins with at least one
            // destination or refuses. Reaching zero mid-run is every
            // destination failing, and the last failure already threw.
            throw new IOException(
                $"Every destination of set '{_setId}' failed mid-run; nothing remains to write to.");
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

        return OpenReadResult.NotFound;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var value = prefix.Value;
        if (value.Length > 0 && !IsBlobKey(value) && !"blobs/".StartsWith(value, StringComparison.Ordinal))
        {
            await foreach (var entry in _metadata.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            yield break;
        }

        if (value.Length == 0)
        {
            await foreach (var entry in _metadata.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
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
            if (configuration.FindDestination(reference.Ref) is
                { Kind: DestinationKind.LocalPath, AddressDefect: null } destination
                && Directory.Exists(destination.Path))
            {
                holders.Add(new Shipment(
                    destination.Name,
                    ReplicaStoreFor(destination),
                    reference.Priority ?? destination.Priority ?? 0));
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
