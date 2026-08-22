using FallbackPlan.Repository;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
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
/// staging archive with its own gapless sequence — and this type is the unit
/// that multiplies; the runtime keeps everything that stays singular (the
/// state-directory lock, the job journal, the queue, the progress hub).
/// </remarks>
public sealed class ArchiveHandle : IDisposable
{
    /// <summary>The archive's object store.</summary>
    public required LocalFileSystemObjectStore Store { get; init; }

    /// <summary>The unlocked repository.</summary>
    public required OpenedRepository Repository { get; init; }

    /// <summary>The writer-side catalogue. Read paths open their own connection.</summary>
    public required FallbackPlan.Repository.Catalogue.Catalogue Catalogue { get; init; }

    /// <summary>The archive's sequence allocator — one per writer role, held here and nowhere else.</summary>
    public required WriterSequence Sequence { get; init; }

    /// <summary>Where this archive's blobs spool before upload.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>The catalogue's path, for read paths opening their own connection.</summary>
    public required string CataloguePath { get; init; }

    /// <summary>The catalogue's logger, so a read connection is as diagnosable as the writer's.</summary>
    public ILogger? CatalogueLogger { get; init; }

    /// <summary>Opens a second catalogue connection for a read path (ADR-0029 §4).</summary>
    /// <returns>A catalogue the caller owns and must dispose.</returns>
    public CatalogueDb OpenReadCatalogue() =>
        CatalogueDb.Open(CataloguePath, Repository.RepositoryId, CatalogueLogger);

    /// <inheritdoc />
    public void Dispose()
    {
        Catalogue.Dispose();
        Repository.Dispose();
    }
}
