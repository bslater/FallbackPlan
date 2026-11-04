using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// The paths a snapshot's failures name, read as folder reuse reads them
/// (ADR-0186): the next backup walks the folder each lies in, to try it
/// again. The count of what that backup will take reads them the same way
/// (ADR-0192 Amendment 1).
/// </summary>
public static class SnapshotFailures
{
    /// <summary>
    /// The paths the snapshot whose manifest is <paramref name="snapshotObjectId"/>
    /// failed, as it spells them: none where it failed nothing, and null where
    /// its manifest or its failures will not read, which a backup answers by
    /// walking in full.
    /// </summary>
    /// <param name="store">The store that holds the snapshot's records.</param>
    /// <param name="repositoryId">The repository they belong to.</param>
    /// <param name="keys">The keys that read them.</param>
    /// <param name="catalogue">The catalogue that says where each record lies.</param>
    /// <param name="snapshotObjectId">The snapshot's manifest.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The failed paths, or null where they cannot be read.</returns>
    public static async ValueTask<IReadOnlyList<string>?> PathsAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        Catalogue.Catalogue catalogue,
        ObjectId snapshotObjectId,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(catalogue);

        using var reader = new TargetedRecordReader(store, repositoryId, keys, new CatalogueGate(catalogue));
        if (await reader.TryReadSnapshotAsync(snapshotObjectId, cancellationToken).ConfigureAwait(false) is not { } manifest)
        {
            return null;
        }

        return manifest.ErrorManifest is { } errors
            ? await reader.TryReadFailedPathsAsync(errors, cancellationToken).ConfigureAwait(false)
            : [];
    }
}
