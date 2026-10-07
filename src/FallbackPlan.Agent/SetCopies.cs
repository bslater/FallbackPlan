using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The copies of one set's blobs other than the one being repaired or read,
/// nearest and cheapest first: the staging archive when it is not itself the
/// one, the set's local-path destinations by priority, its S3-compatible
/// stores, then its paired peers over the retrieval session. What a repair replaces a damaged replica
/// object from (FR-VER-007), and what a restore reads a record from when its
/// own store will not serve it (FR-RST-007).
/// </summary>
/// <remarks>
/// <para>
/// One list for both, so the two cannot disagree about where a sound copy
/// may be. Each copy is opened on first use and at most once: a peer is
/// dialled only when every nearer copy has failed to serve, and a local path
/// whose replica directory is missing is a copy that could not be reached.
/// </para>
/// <para>
/// A copy is read through its destination's transfer limit when nobody is
/// waiting, as every background byte to or from a destination is
/// (ADR-0074); a person's repair or restore is never paced.
/// </para>
/// </remarks>
internal sealed class SetCopies : IAsyncDisposable
{
    /// <summary>How the staging archive is named among the copies.</summary>
    public const string StagingName = "the staging archive";

    private readonly ServiceRuntime _runtime;
    private readonly ArchiveHandle _archive;
    private readonly bool _userInitiated;
    private readonly List<IAsyncDisposable> _sessions = [];
    private readonly Dictionary<string, DestinationConfiguration> _destinations = new(StringComparer.Ordinal);

    /// <summary>Lists the copies of one set.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose blobs these are copies of.</param>
    /// <param name="archive">The set's archive, for its repository and — for a staging set — its staging copy.</param>
    /// <param name="excluding">A destination that is never a copy here — the one being repaired; null for none.</param>
    /// <param name="includeStaging">Whether the staging archive is a copy: false when it is the one being read, or the set has none.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background reader reads each copy through its limit.</param>
    public SetCopies(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        ArchiveHandle archive,
        string? excluding,
        bool includeStaging,
        bool userInitiated)
    {
        _runtime = runtime;
        _archive = archive;
        _userInitiated = userInitiated;

        var sources = new List<CopySource>();
        if (includeStaging)
        {
            sources.Add(new CopySource(StagingName, _ => ValueTask.FromResult<IObjectStore?>(archive.Store)));
        }

        var configuration = runtime.Configuration;
        var siblings = set.Destinations
            .Where(reference => !string.Equals(reference.Ref, excluding, StringComparison.Ordinal))
            .Select(reference => (Reference: reference, Destination: configuration.FindDestination(reference.Ref)))
            .Where(sibling => sibling.Destination is { AddressDefect: null })
            .OrderByDescending(sibling => SetDestinationReference.EffectivePriority(sibling.Reference, sibling.Destination))
            .Select(sibling => sibling.Destination!)
            .ToList();

        foreach (var sibling in siblings.Where(sibling => sibling.Kind == DestinationKind.LocalPath))
        {
            sources.Add(Once(sibling, _ => OpenLocal(sibling)));
        }

        foreach (var sibling in siblings.Where(sibling => sibling.Kind == DestinationKind.S3))
        {
            sources.Add(Once(sibling, _ => OpenS3(sibling)));
        }

        foreach (var sibling in siblings.Where(sibling => sibling.Kind == DestinationKind.Peer))
        {
            sources.Add(Once(sibling, token => DialAsync(sibling, token)));
        }

        Sources = sources;
    }

    /// <summary>The copies, in the order to try them.</summary>
    public IReadOnlyList<CopySource> Sources { get; }

    /// <summary>How a destination is named among the copies.</summary>
    public static string NameOf(DestinationConfiguration destination) => $"destination '{destination.Name}'";

    /// <summary>The destination a copy of that name is, or null for the staging archive or a name not listed.</summary>
    public DestinationConfiguration? DestinationNamed(string sourceName) =>
        _destinations.GetValueOrDefault(sourceName);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A destination's copy, opened on first use and remembered — its store,
    /// or the reason it could not be reached, which is the same reason for
    /// every later blob.
    /// </summary>
    private CopySource Once(DestinationConfiguration destination, Func<CancellationToken, ValueTask<IObjectStore?>> open)
    {
        var name = NameOf(destination);
        _destinations[name] = destination;
        Task<IObjectStore?>? opened = null;
        return new CopySource(name, token => new ValueTask<IObjectStore?>(opened ??= open(token).AsTask()));
    }

    private ValueTask<IObjectStore?> OpenLocal(DestinationConfiguration destination)
    {
        var root = Path.Combine(destination.Path!, _archive.Repository.RepositoryId.ToString());
        return ValueTask.FromResult<IObjectStore?>(
            Directory.Exists(root)
                ? PacedObjectStore.Over(new LocalFileSystemObjectStore(root), Limiter(destination))
                : null);
    }

    private ValueTask<IObjectStore?> OpenS3(DestinationConfiguration destination)
    {
        // A store with no key stored, or one whose key is damaged, is a copy
        // that cannot be reached; the next copy, or none, is the truth.
        Storage.S3.S3ObjectStore? store;
        try
        {
            store = StoreComposition.OpenS3(
                _runtime, destination, _archive.Repository.RepositoryId.ToString(), out _);
        }
        catch (Domain.ClientStateException)
        {
            store = null;
        }

        return ValueTask.FromResult<IObjectStore?>(
            store is null ? null : PacedObjectStore.Over(store, Limiter(destination)));
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
