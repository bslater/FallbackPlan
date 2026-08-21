using System.Collections.Concurrent;
using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// One open restore source (ADR-0041): a per-set repository the guided
/// restore reads from — the set's own staging archive (borrowing the
/// runtime's open handle), a local-path destination's replica, or a paired
/// peer's replica over the retrieval session — with the catalogue that
/// answers listings and plans for it.
/// </summary>
internal sealed class OpenRestoreSourceHandle : IAsyncDisposable
{
    /// <summary>The handle clients present, 16 hex.</summary>
    public required string SourceId { get; init; }

    /// <summary>The set whose repository this is, 32-hex id.</summary>
    public required string SetId { get; init; }

    /// <summary>The set's name, for results.</summary>
    public required string SetName { get; init; }

    /// <summary><c>staging</c>, or the destination's declared name.</summary>
    public required string Location { get; init; }

    /// <summary>The store reads go through.</summary>
    public required Storage.Abstractions.IObjectStore Store { get; init; }

    /// <summary>
    /// The repository this source opened for itself — owned, disposed with
    /// the handle. Null for a staging source, which borrows the runtime's
    /// archive and owns nothing.
    /// </summary>
    public OpenedRepository? OwnedRepository { get; init; }

    /// <summary>The repository's identity.</summary>
    public required RepositoryId RepositoryId { get; init; }

    /// <summary>The unwrapped keys — borrowed from whichever open owns them.</summary>
    public required RepositoryKeySet Keys { get; init; }

    /// <summary>The catalogue file read connections open against.</summary>
    public required string CataloguePath { get; init; }

    /// <summary>The catalogue's logger, carried so a read connection is diagnosable too.</summary>
    public ILogger? CatalogueLogger { get; init; }

    /// <summary>
    /// This source's throwaway cache directory under
    /// <c>&lt;state&gt;/restore-cache/</c> — deleted on dispose. Null for a
    /// staging source, whose catalogue is the archive's own.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// What must go when this source does — the peer session, once phase 4
    /// wires one under <see cref="Store"/>.
    /// </summary>
    public IAsyncDisposable? Transport { get; init; }

    /// <summary>
    /// A write-only set's granted read authority (ADR-0042 §5): the sealing
    /// scalar that arrived sealed on the open, alive exactly as long as this
    /// handle — zeroed by the explicit close, the idle sweep, or shutdown.
    /// Null on v1 sources and on grant-less (structure-only) opens.
    /// </summary>
    public RepositoryReadAuthority? ReadAuthority { get; set; }

    /// <summary>Unix ms of last use, for the idle sweep.</summary>
    public long LastTouchedMs;

    /// <summary>
    /// One command at a time per source: the sweep and an in-flight plan must
    /// not race a dispose.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>A read-side catalogue connection.</summary>
    public CatalogueDb OpenReadCatalogue() => CatalogueDb.Open(CataloguePath, RepositoryId, CatalogueLogger);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        ReadAuthority?.Dispose();
        OwnedRepository?.Dispose();
        if (Transport is not null)
        {
            await Transport.DisposeAsync().ConfigureAwait(false);
        }

        if (CacheDirectory is not null && Directory.Exists(CacheDirectory))
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(CacheDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cache directory that outlives its source is reclaimed by
                // the start-up purge; failing a dispose over it would turn
                // cleanup into breakage.
            }
        }

        Gate.Dispose();
    }
}

/// <summary>
/// The open restore sources, keyed by handle (ADR-0041). Sources expire
/// after idle disuse — swept opportunistically on the verbs that touch the
/// registry, because a wizard abandoned mid-flight must not hold a replica
/// open forever — and every survivor is disposed with the runtime.
/// </summary>
internal sealed class RestoreSourceRegistry : IAsyncDisposable
{
    /// <summary>How long an untouched source lives.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, OpenRestoreSourceHandle> _sources = new(StringComparer.Ordinal);

    /// <summary>Registers a freshly opened source.</summary>
    public void Add(OpenRestoreSourceHandle handle)
    {
        ThrowHelper.ThrowIfNull(handle);
        handle.LastTouchedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _sources[handle.SourceId] = handle;
    }

    /// <summary>Resolves a handle and touches it; null when unknown or expired.</summary>
    public OpenRestoreSourceHandle? Find(string sourceId)
    {
        if (!_sources.TryGetValue(sourceId, out var handle))
        {
            return null;
        }

        handle.LastTouchedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return handle;
    }

    /// <summary>Closes one source; idempotent — closing the closed acknowledges.</summary>
    public async ValueTask CloseAsync(string sourceId)
    {
        if (_sources.TryRemove(sourceId, out var handle))
        {
            // Wait for any in-flight command before tearing the source down.
            await handle.Gate.WaitAsync().ConfigureAwait(false);
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes sources idle past <see cref="IdleLifetime"/>. A source whose
    /// gate is held is skipped — the in-flight command's touch renews it.
    /// </summary>
    public async ValueTask SweepAsync()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)IdleLifetime.TotalMilliseconds;
        foreach (var (sourceId, handle) in _sources)
        {
            if (Interlocked.Read(ref handle.LastTouchedMs) < cutoff
                && handle.Gate.CurrentCount > 0
                && _sources.TryRemove(sourceId, out var removed))
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var (sourceId, _) in _sources)
        {
            if (_sources.TryRemove(sourceId, out var handle))
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
