using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// Repairs one local-path replica's damaged blobs from the other copies its
/// set has (FR-VER-007, ADR-0035 Amendment 1): the staging archive, the
/// set's other local-path destinations, then its paired peers over the
/// retrieval session — nearest and cheapest first, as <see cref="SetCopies"/>
/// lists them for a restore too. In reverse, it repairs the staging archive
/// itself from the set's destinations (FR-VER-009, ADR-0095).
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
    private readonly string? _excluding;
    private readonly bool _includeStaging;
    private readonly bool _includePeers;
    private readonly ArchiveHandle _archive;
    private readonly bool _userInitiated;
    private readonly string _scratchRoot;
    private IObjectStore? _scratch;
    private SetCopies? _copies;

    /// <summary>Creates a repairer for one destination of one set.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica is damaged.</param>
    /// <param name="damagedDestination">The destination holding the damage; never a source.</param>
    /// <param name="archive">The set's archive, for its keys and — for a staging set — its staging copy.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background repair reads its sources through their limits.</param>
    public ReplicaRepairer(
        ServiceRuntime runtime, BackupSetConfiguration set, string damagedDestination, ArchiveHandle archive,
        bool userInitiated)
        // A staging set's staging archive is the copy every destination was
        // filled from. A direct-ship set has none: its store reads blobs back
        // from the destinations, this one included.
        : this(runtime, set, archive, damagedDestination, includeStaging: archive.ShipSink is null, userInitiated)
    {
    }

    private ReplicaRepairer(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive, string? excluding,
        bool includeStaging, bool userInitiated, bool includePeers = true)
    {
        _runtime = runtime;
        _set = set;
        _excluding = excluding;
        _includeStaging = includeStaging;
        _includePeers = includePeers;
        _archive = archive;
        _userInitiated = userInitiated;
        _scratchRoot = Path.Combine(runtime.Options.StateDirectory, "spool", "repair", Guid.NewGuid().ToString("n"));
    }

    /// <summary>
    /// A repairer for a staging set's staging archive, whose sources are the
    /// set's destinations, every one of them: none holds the damage being
    /// repaired, and each copy is proven before it is used.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose staging archive is damaged.</param>
    /// <param name="archive">The set's archive, for its keys.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background repair reads its sources through their limits.</param>
    public static ReplicaRepairer ForStaging(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive, bool userInitiated) =>
        new(runtime, set, archive, excluding: null, includeStaging: false, userInitiated);

    /// <summary>
    /// A repairer for a peer's replica (ADR-0100), whose sources are the
    /// copies this installation reads without dialling a peer: the staging
    /// archive and the set's local paths and stores. A finding at one peer is
    /// no reason to put a session on another's link — and a refusal by the
    /// peer, which its own copy decides, would spend that link again on every
    /// sync that offers the repair.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica at the peer is damaged.</param>
    /// <param name="peerName">The peer holding the damage; never a source.</param>
    /// <param name="archive">The set's archive, for its keys and — for a staging set — its staging copy.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background repair reads its sources through their limits.</param>
    public static ReplicaRepairer ForPeer(
        ServiceRuntime runtime, BackupSetConfiguration set, string peerName, ArchiveHandle archive,
        bool userInitiated) =>
        new(runtime, set, archive, peerName, includeStaging: archive.ShipSink is null, userInitiated, includePeers: false);

    /// <summary>Repairs each key at the target, in order, and says what became of each.</summary>
    /// <param name="target">Where a proven copy is put, and read back from.</param>
    /// <param name="keys">The keys found damaged there.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    public async Task<IReadOnlyList<ReplicaRepairOutcome>> RepairAsync(
        IRepairTarget target, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var outcomes = new List<ReplicaRepairOutcome>();
        foreach (var key in keys)
        {
            outcomes.Add(await ReplicaRepair.RepairAsync(
                _archive.Repository.RepositoryId, _archive.Repository.Keys, target, ObjectKey.Parse(key), Sources(),
                Scratch(), cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
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
        if (_copies is not null)
        {
            await _copies.DisposeAsync().ConfigureAwait(false);
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
        IObjectStore replica, ObjectKey key, CancellationToken cancellationToken) =>
        ReplicaRepair.RepairAsync(
            _archive.Repository.RepositoryId, _archive.Repository.Keys, replica, key, Sources(), Scratch(),
            cancellationToken);

    /// <summary>The stage, made on first use.</summary>
    private IObjectStore Scratch()
    {
        if (_scratch is null)
        {
            Directory.CreateDirectory(_scratchRoot);
            _scratch = new LocalFileSystemObjectStore(_scratchRoot);
        }

        return _scratch;
    }

    /// <summary>The sources, listed once, each opened at most once.</summary>
    private IReadOnlyList<CopySource> Sources() =>
        (_copies ??= new SetCopies(
            _runtime, _set, _archive, _excluding, _includeStaging, _userInitiated, _includePeers)).Sources;
}
