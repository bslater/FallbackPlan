using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// Rebuilding a catalogue from a repository's objects: index-plane rebuild
/// for locations, manifest projection for snapshots and paths (FR-MAN-002).
/// Three callers, one rebuild: the restore source builds its throwaway copy
/// (ADR-0041), archive adoption builds the set's real one (ADR-0061), and
/// the heal after a whole-directory rollback re-projects the live one in
/// place over a destination's newer history (ADR-0062).
/// </summary>
internal static class CatalogueRebuild
{
    /// <summary>
    /// A reader over a repository's metadata blobs, loaded and ready to
    /// answer manifest reads (the metadata-class footers only). The caller
    /// disposes.
    /// </summary>
    /// <param name="store">The repository, or a replica of it.</param>
    /// <param name="repository">The opened repository.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    internal static async ValueTask<RepositoryReader> OpenMetadataReaderAsync(
        IObjectStore store, OpenedRepository repository, CancellationToken cancellationToken)
    {
        var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        try
        {
            var metadataBlobs = new List<ObjectKey>();
            await foreach (var blob in store.ListAsync(
                ObjectPrefix.Parse("blobs/meta/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
            {
                metadataBlobs.Add(blob.Key);
            }

            await reader.LoadBlobsAsync(metadataBlobs, cancellationToken).ConfigureAwait(false);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a catalogue at <paramref name="cataloguePath"/> and rebuilds it
    /// from the repository in <paramref name="store"/>. Rebuild findings land
    /// in <paramref name="warnings"/>; the caller disposes the open catalogue.
    /// </summary>
    /// <param name="runtime">For the loggers.</param>
    /// <param name="store">The repository, or a replica of it.</param>
    /// <param name="repository">The opened repository.</param>
    /// <param name="cataloguePath">Where the catalogue file lives.</param>
    /// <param name="reader">A reader already loaded with the metadata blobs.</param>
    /// <param name="warnings">Where rebuild findings are appended.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    internal static async ValueTask<CatalogueDb> OpenRebuiltAsync(
        ServiceRuntime runtime,
        IObjectStore store,
        OpenedRepository repository,
        string cataloguePath,
        RepositoryReader reader,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var catalogue = CatalogueDb.Open(cataloguePath, repository.RepositoryId, runtime.LoggerFor<CatalogueDb>());
        try
        {
            await RebuildIntoAsync(runtime, catalogue, store, repository, reader, warnings, cancellationToken)
                .ConfigureAwait(false);
            return catalogue;
        }
        catch
        {
            catalogue.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds <paramref name="catalogue"/> in place from the repository in
    /// <paramref name="store"/>. Every write the rebuild makes is an upsert
    /// or an insert-or-ignore, so re-projecting over a catalogue that
    /// already holds part of the history adds what is missing and disturbs
    /// nothing that is there — which is what lets the heal run over the
    /// runtime's live handle rather than evicting it.
    /// </summary>
    /// <param name="runtime">For the loggers.</param>
    /// <param name="catalogue">The open catalogue to rebuild into.</param>
    /// <param name="store">The repository, or a replica of it.</param>
    /// <param name="repository">The opened repository.</param>
    /// <param name="reader">A reader already loaded with the metadata blobs.</param>
    /// <param name="warnings">Where rebuild findings are appended.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    internal static async ValueTask RebuildIntoAsync(
        ServiceRuntime runtime,
        CatalogueDb catalogue,
        IObjectStore store,
        OpenedRepository repository,
        RepositoryReader reader,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var generation = Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);

        // The index plane rebuilds the locations; no blob inventory is
        // taken, because an entry naming a trimmed blob is re-answered
        // honestly by the plan probe, which asks the store per blob
        // (FR-RST-003).
        var report = await new CatalogueRebuilder(
            new IndexLoader(store, repository.RepositoryId, repository.Credential, runtime.LoggerFor<IndexLoader>()),
            runtime.LoggerFor<CatalogueRebuilder>())
            .RebuildAsync(
                catalogue, generation, gapPatienceGenerations: 2,
                isSequenceAccountedAsync: null, cancellationToken)
            .ConfigureAwait(false);

        await CatalogueProjector.ProjectAsync(
            catalogue, reader, store, repository.RepositoryId, repository.Keys,
            repository.Credential, cancellationToken).ConfigureAwait(false);

        warnings.AddRange(report.Findings.Select(finding => $"{finding.Kind}: {finding.Detail}"));
    }
}
