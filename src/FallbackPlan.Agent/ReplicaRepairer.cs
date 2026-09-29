using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// Repairs one local-path replica's damaged blobs from the other copies its
/// set has (FR-VER-007, ADR-0035 Amendment 1): the staging archive, the
/// set's other local-path destinations, then its paired peers over the
/// retrieval session — nearest and cheapest first.
/// </summary>
/// <remarks>
/// <para>
/// The damaged destination is never a source, and neither is a direct-ship
/// set's own read path. The ship sink answers a blob from the
/// highest-priority destination holding it, which may be exactly the one
/// being repaired, so the siblings are opened one by one here instead.
/// </para>
/// <para>
/// A sibling is read through its own transfer limit when nobody is waiting,
/// as every background byte to or from a destination is (ADR-0074). A peer is
/// dialled only when every nearer source has failed to serve, and at most once
/// per repairer however many blobs it is asked about.
/// </para>
/// <para>
/// Each source's copy is staged under the state directory's spool before it
/// is proven, so the bytes proven are the bytes installed and a peer's link is
/// read once. The stage holds one blob at a time and is removed when the
/// repairer is.
/// </para>
/// </remarks>
internal sealed class ReplicaRepairer : IAsyncDisposable
{
    private readonly ServiceRuntime _runtime;
    private readonly BackupSetConfiguration _set;
    private readonly string _damagedDestination;
    private readonly ArchiveHandle _archive;
    private readonly bool _userInitiated;
    private readonly List<IAsyncDisposable> _sessions = [];
    private readonly string _scratchRoot;
    private IObjectStore? _scratch;
    private List<RepairSource>? _sources;

    /// <summary>Creates a repairer for one destination of one set.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica is damaged.</param>
    /// <param name="damagedDestination">The destination holding the damage; never a source.</param>
    /// <param name="archive">The set's archive, for its keys and — for a staging set — its staging copy.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background repair reads its sources through their limits.</param>
    public ReplicaRepairer(
        ServiceRuntime runtime, BackupSetConfiguration set, string damagedDestination, ArchiveHandle archive,
        bool userInitiated)
    {
        _runtime = runtime;
        _set = set;
        _damagedDestination = damagedDestination;
        _archive = archive;
        _userInitiated = userInitiated;
        _scratchRoot = Path.Combine(runtime.Options.StateDirectory, "spool", "repair", Guid.NewGuid().ToString("n"));
    }

    /// <summary>Repairs each key at the replica, in order, and says what became of each.</summary>
    /// <param name="replica">The damaged destination's store.</param>
    /// <param name="keys">The keys found damaged there.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    public async Task<IReadOnlyList<ReplicaRepairOutcome>> RepairAsync(
        IObjectStore replica, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var outcomes = new List<ReplicaRepairOutcome>();
        foreach (var key in keys)
        {
            outcomes.Add(await RepairOneAsync(replica, ObjectKey.Parse(key), cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>
    /// Re-reads the keys the ledger holds as damaged at the replica — the
    /// repair sync ADR-0035 promised — and repairs what can be repaired now.
    /// </summary>
    /// <param name="replica">The destination's store.</param>
    /// <param name="keys">The keys outstanding on the ledger.</param>
    /// <param name="owed">
    /// Whether the destination is still owed a key it no longer holds. One its
    /// policy has let go is resolved, not refilled: refilling it would undo
    /// the convergence that removed it, on every sync, for ever.
    /// </param>
    /// <param name="cancellationToken">Cancels the re-check.</param>
    /// <returns>The keys no longer outstanding, and what could not be repaired and why.</returns>
    public async Task<(IReadOnlyList<string> Resolved, IReadOnlyList<ReplicaRepairOutcome> Unrepaired)> RecheckAsync(
        IObjectStore replica, IReadOnlyList<string> keys, Func<string, bool> owed, CancellationToken cancellationToken)
    {
        var resolved = new List<string>();
        var unrepaired = new List<ReplicaRepairOutcome>();
        foreach (var key in keys)
        {
            var storeKey = ObjectKey.Parse(key);
            var held = await ReplicaRepair
                .ProveAsync(_archive.Repository.RepositoryId, _archive.Repository.Keys, replica, storeKey, cancellationToken)
                .ConfigureAwait(false);
            if (held.Sound || (!held.Held && !owed(key)))
            {
                resolved.Add(key);
                continue;
            }

            var outcome = await RepairOneAsync(replica, storeKey, cancellationToken).ConfigureAwait(false);
            if (outcome.Repaired)
            {
                resolved.Add(key);
            }
            else
            {
                unrepaired.Add(outcome);
            }
        }

        return (resolved, unrepaired);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stage that will not delete holds at most one blob, and the
            // next service start's spool is a different directory.
        }
    }

    private ValueTask<ReplicaRepairOutcome> RepairOneAsync(
        IObjectStore replica, ObjectKey key, CancellationToken cancellationToken)
    {
        if (_scratch is null)
        {
            Directory.CreateDirectory(_scratchRoot);
            _scratch = new LocalFileSystemObjectStore(_scratchRoot);
        }

        return ReplicaRepair.RepairAsync(
            _archive.Repository.RepositoryId, _archive.Repository.Keys, replica, key, Sources(), _scratch,
            cancellationToken);
    }

    /// <summary>The sources, built once, each opened at most once.</summary>
    private List<RepairSource> Sources()
    {
        if (_sources is not null)
        {
            return _sources;
        }

        _sources = [];
        if (_archive.ShipSink is null)
        {
            // A staging set's staging archive is the copy every destination
            // was filled from. A direct-ship set has none: its store reads
            // blobs back from the destinations, this one included.
            _sources.Add(new RepairSource(
                "the staging archive", _ => ValueTask.FromResult<IObjectStore?>(_archive.Store)));
        }

        var configuration = _runtime.Configuration;
        var siblings = _set.Destinations
            .Where(reference => !string.Equals(reference.Ref, _damagedDestination, StringComparison.Ordinal))
            .Select(reference => (Reference: reference, Destination: configuration.FindDestination(reference.Ref)))
            .Where(sibling => sibling.Destination is { AddressDefect: null })
            .OrderByDescending(sibling => SetDestinationReference.EffectivePriority(sibling.Reference, sibling.Destination))
            .Select(sibling => sibling.Destination!)
            .ToList();

        foreach (var sibling in siblings.Where(sibling => sibling.Kind == DestinationKind.LocalPath))
        {
            _sources.Add(Once($"destination '{sibling.Name}'", _ => OpenLocal(sibling)));
        }

        foreach (var sibling in siblings.Where(sibling => sibling.Kind == DestinationKind.Peer))
        {
            _sources.Add(Once($"destination '{sibling.Name}'", token => DialAsync(sibling, token)));
        }

        return _sources;
    }

    /// <summary>
    /// A source opened on first use and remembered — its store, or the reason
    /// it could not be reached, which is the same reason for every later blob.
    /// </summary>
    private static RepairSource Once(string name, Func<CancellationToken, ValueTask<IObjectStore?>> open)
    {
        Task<IObjectStore?>? opened = null;
        return new RepairSource(name, token => new ValueTask<IObjectStore?>(opened ??= open(token).AsTask()));
    }

    private ValueTask<IObjectStore?> OpenLocal(DestinationConfiguration destination)
    {
        var root = Path.Combine(destination.Path!, _archive.Repository.RepositoryId.ToString());
        return ValueTask.FromResult<IObjectStore?>(
            Directory.Exists(root)
                ? PacedObjectStore.Over(new LocalFileSystemObjectStore(root), Limiter(destination))
                : null);
    }

    private async ValueTask<IObjectStore?> DialAsync(DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        var client = await PeerRetrievalClient.DialAsync(
            _runtime, destination, _archive.Repository.RepositoryId.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        _sessions.Add(client);
        return PacedObjectStore.Over(new PeerRetrievalObjectStore(client), Limiter(destination));
    }

    private ByteRateLimiter? Limiter(DestinationConfiguration destination) =>
        _userInitiated ? null : _runtime.Pacing.ForDestination(destination);
}
