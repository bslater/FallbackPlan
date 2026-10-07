using Bodu;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using DamageReach = FallbackPlan.Repository.Catalogue.DamageReach;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// Everything the service holds open for one repository archive: the store,
/// the unlocked repository, the writer-side catalogue, the one sequence
/// allocator for the archive's writer role, and the spool the writer fills.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ServiceRuntime"/>'s flat fields so that "an
/// archive and its writer-side state" is one value with one lifetime. Under
/// ADR-0034 the service holds one of these per backup set — each set's
/// archive, staging or direct-ship (ADR-0046), with its own gapless sequence
/// — and this type is the unit that multiplies; the runtime keeps everything that stays singular (the
/// state-directory lock, the job journal, the queue, the progress hub).
/// </remarks>
public sealed class ArchiveHandle : IDisposable
{
    /// <summary>
    /// The archive's object store: the staging archive's local filesystem
    /// store, or — for a direct-ship set (ADR-0046) — the
    /// <see cref="DestinationShipSink"/> that fans writes to the set's
    /// destinations and keeps only metadata locally.
    /// </summary>
    public required IObjectStore Store { get; init; }

    /// <summary>
    /// The ship sink, when this archive direct-ships; null for a staging
    /// archive. The same object as <see cref="Store"/>, typed for the run
    /// hooks (<see cref="DestinationShipSink.BeginRunAsync"/> /
    /// <see cref="DestinationShipSink.CompleteRunAsync"/>) only the backup runner
    /// calls.
    /// </summary>
    public DestinationShipSink? ShipSink { get; init; }

    /// <summary>The unlocked repository.</summary>
    public required OpenedRepository Repository { get; init; }

    /// <summary>
    /// The catalogue connection the set's backup reads and writes through,
    /// and nothing else does once the handle is shared.
    /// </summary>
    /// <remarks>
    /// A sync, retention, a snapshot deletion and a heal can each run while
    /// the set's backup is running, and a SQLite connection is not safe to
    /// share between threads: shared, it corrupts its own bookkeeping and
    /// fails later and elsewhere. Each opens a connection of its own instead
    /// — <see cref="OpenReadCatalogue"/> to read, <see cref="OpenWritableCatalogue"/>
    /// to write — and SQLite keeps the connections apart (ADR-0010 Amendment 5).
    /// </remarks>
    public required FallbackPlan.Repository.Catalogue.Catalogue Catalogue { get; init; }

    /// <summary>The archive's sequence allocator — one per writer role, held here and nowhere else.</summary>
    public required WriterSequence Sequence { get; init; }

    /// <summary>Where this archive's blobs spool before upload.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>The catalogue's path, for read paths opening their own connection.</summary>
    public required string CataloguePath { get; init; }

    /// <summary>The catalogue's logger, so a read connection is as diagnosable as the writer's.</summary>
    public ILogger? CatalogueLogger { get; init; }

    /// <summary>
    /// Whether this archive was opened with the installation <em>passphrase</em>
    /// rather than a stored write credential — that is, whether this process
    /// can still reach the Argon2id root behind it (peer-protocol 03 §3.2.1).
    /// </summary>
    /// <remarks>
    /// A provisioned write-only set opens from the bundle its root produced and
    /// not from the root, so it answers false here even when the runtime holds
    /// some passphrase: the one it holds need not be this archive's. Arming a
    /// replica claim is the only thing that asks, and declining is the correct
    /// answer for it (ADR-0042 §1, Q23).
    /// </remarks>
    public bool OpenedWithPassphrase { get; init; }

    /// <summary>Opens a second catalogue connection for a read path (ADR-0029 §4).</summary>
    /// <remarks>
    /// A read on it sees the catalogue's last commit and never waits behind
    /// the backup's write.
    /// </remarks>
    /// <returns>A catalogue the caller owns and must dispose.</returns>
    public CatalogueDb OpenReadCatalogue() =>
        CatalogueDb.Open(CataloguePath, Repository.RepositoryId, CatalogueLogger);

    /// <summary>
    /// Opens a catalogue connection for a path that writes the set's
    /// catalogue while its backup may be running: retention, a snapshot
    /// deletion, a heal (ADR-0010 Amendment 5).
    /// </summary>
    /// <remarks>
    /// SQLite lets one connection write at a time. A write here behind the
    /// backup's waits for it to commit, and the backup's behind this one's
    /// waits likewise, up to the command timeout of thirty seconds. Every
    /// catalogue call commits before it returns, so a write waits for the
    /// other side's calls, never for the whole of its job.
    /// </remarks>
    /// <returns>A catalogue the caller owns and must dispose.</returns>
    public CatalogueDb OpenWritableCatalogue() =>
        CatalogueDb.Open(CataloguePath, Repository.RepositoryId, CatalogueLogger);

    /// <summary>
    /// What damage to the blobs stored under <paramref name="damagedKeys"/>
    /// reaches, as this set's catalogue traces it (FR-VER-005), read on a
    /// connection of its own.
    /// </summary>
    /// <remarks>
    /// A catalogue that cannot be read traces nothing, and says so: every key
    /// comes back untraced, which counts every snapshot as reached rather than
    /// none.
    /// </remarks>
    public DamageReach TraceDamage(IReadOnlyCollection<string> damagedKeys)
    {
        ThrowHelper.ThrowIfNull(damagedKeys);

        try
        {
            using var catalogue = OpenReadCatalogue();
            return DamageScope.Trace(catalogue, Repository.Keys, damagedKeys);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or IOException)
        {
            return DamageReach.None with { Untraced = damagedKeys.Count };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Catalogue.Dispose();
        Repository.Dispose();
    }
}
