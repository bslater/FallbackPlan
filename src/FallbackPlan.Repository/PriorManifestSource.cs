using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// One publication's targeted read path for file-version manifests it
/// already wrote: given an object identifier, the manifest, without a
/// whole-repository blob enumeration.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a file that only <em>moved</em> does not have its content
/// re-read. A rename needs a new manifest, because a manifest states its own
/// name (specification 06 §4), but everything else in it — the segment
/// references, the whole-file hash, the length — is unchanged and already
/// durable. Fetching one record is a few kilobytes; re-reading the file is
/// the whole file.
/// </para>
/// <para>
/// The catalogue resolves the location (07 §3 precedence) and
/// <see cref="BlobReader"/> opens the one blob it names, so the cost is two
/// range reads plus one small envelope read per blob and one range read per
/// record — never a listing. Readers are cached for the run because a
/// directory renamed wholesale puts every child's prior manifest in the same
/// few blobs.
/// </para>
/// <para>
/// Every failure yields <see langword="null"/> and nothing else. A manifest
/// that cannot be fetched, authenticated, or decoded is not a damage finding
/// here: the caller's fallback is to read the file, which is what it would
/// have done anyway. Damage in a blob this publication does not depend on is
/// <c>verify</c>'s business, and reporting it from the capture path would
/// fail a backup over an object nobody asked for.
/// </para>
/// </remarks>
internal sealed class PriorManifestSource(
    IObjectStore store,
    RepositoryId repositoryId,
    RepositoryKeySet keys,
    Catalogue.Catalogue catalogue) : IDisposable
{
    private readonly ObjectIdDeriver _objectIdDeriver = new(keys.ContentIdKey);
    private readonly Dictionary<ObjectKey, BlobReader?> _blobs = [];

    /// <summary>
    /// The file-version manifest <paramref name="objectId"/> names, or
    /// <see langword="null"/> when it cannot be read for any reason.
    /// </summary>
    public async ValueTask<FileVersionManifest?> TryReadAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        if (catalogue.ResolveLocation(objectId) is not
            {
                StoreBlobKey: { } storeBlobKey,
                BlobClass: { } blobClass,
                BlobLength: > 0 and { } blobLength,
            })
        {
            return null;
        }

        if (blobClass is not (BlobClass.Data or BlobClass.Metadata))
        {
            return null;
        }

        var storeKey = BlobStoreKeys.ForBlob(blobClass, storeBlobKey);
        var reader = await OpenAsync(storeKey, blobLength, cancellationToken).ConfigureAwait(false);

        if (reader is null)
        {
            return null;
        }

        // The footer's table is the authority on where the record is, not the
        // catalogue's copy of it: the table is authenticated against the
        // repository and the catalogue is a cache.
        RecordTableEntry located = default;
        var found = false;
        foreach (var entry in reader.RecordTable)
        {
            if (entry.ObjectId == objectId)
            {
                located = entry;
                found = true;
                break;
            }
        }

        if (!found || located.ObjectType != ObjectType.FileVersionManifest)
        {
            return null;
        }

        try
        {
            var record = await reader.ReadRecordAsync(located, cancellationToken).ConfigureAwait(false);

            return record.Outcome == RecordReadOutcome.Ok
                ? FileVersionManifestCodec.Decode(record.Plaintext!)
                : null;
        }
        catch (Exception exception) when (
            exception is BlobFormatException or ManifestValidationException or FormatException or IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var reader in _blobs.Values)
        {
            reader?.Dispose();
        }

        _blobs.Clear();
        _objectIdDeriver.Dispose();
    }

    private async ValueTask<BlobReader?> OpenAsync(
        ObjectKey storeKey, long blobLength, CancellationToken cancellationToken)
    {
        if (_blobs.TryGetValue(storeKey, out var cached))
        {
            return cached;
        }

        BlobReader? reader;
        try
        {
            reader = await BlobReader.OpenAsync(
                store, storeKey, blobLength, repositoryId, keys.DeriveClassKey, _objectIdDeriver, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BlobFormatException or IOException)
        {
            // Remembered as unreadable, so a directory of moved files does not
            // retry the same broken blob once per child.
            reader = null;
        }

        _blobs[storeKey] = reader;
        return reader;
    }
}
