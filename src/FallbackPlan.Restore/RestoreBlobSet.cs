using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Restore;

/// <summary>
/// What a plan needs from the store: the blobs it must open, and the paths
/// it cannot reach.
/// </summary>
/// <param name="Blobs">
/// Exactly the blob set a run needs — what the targeted load opens instead
/// of every footer in the store.
/// </param>
/// <param name="Missing">
/// Paths whose manifest or whose segments the store does not hold, reported
/// before any byte moves rather than discovered part-way through
/// (FR-RST-003).
/// </param>
public sealed record RestoreBlobSetResult(
    IReadOnlyCollection<ObjectKey> Blobs,
    IReadOnlyList<string> Missing);

/// <summary>
/// Resolves what a <see cref="RestorePlan"/> needs from the store
/// (NFR-PERF-009; architecture 05 §5): the blob set the run will open, and
/// the paths it cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue alone cannot answer this — it is a cache, and a cache ahead
/// of the store says "nothing missing" about the very objects the store has
/// lost — so each located blob is probed against the store, one memoized
/// metadata call per distinct blob. The manifest blob alone is not enough:
/// an item's <b>segments</b> live in other blobs — after a staging trim,
/// precisely the ones no longer here ([ADR-0034](../../docs/adr/0034-hub-and-spoke-destinations.md) §6)
/// — so each manifest is read (metadata never trims) and its referenced
/// blobs are probed too.
/// </para>
/// <para>
/// This lives beside the planner rather than in the service because every
/// restore path needs it and only one had it: the service used it for a
/// remote source and opened every footer in the store for a local one, and
/// the CLI's direct restore opened every footer always. A load proportional
/// to the repository rather than to the restore is what
/// <c>Repository.Tests/RestoreBreadthTests</c> measures against the
/// NFR-PERF-009 budget.
/// </para>
/// </remarks>
public static class RestoreBlobSet
{
    /// <summary>Resolves the blob set and the unreachable paths for <paramref name="plan"/>.</summary>
    public static async ValueTask<RestoreBlobSetResult> ResolveAsync(
        Catalogue catalogue,
        RestorePlan plan,
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);

        using var keyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        using var objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        using var metaReaders = new MetaReaderCache();
        var blobKeys = new Dictionary<BlobId, ObjectKey?>();
        var missing = new List<string>();
        var needed = new HashSet<ObjectKey>();

        async ValueTask<bool> PresentAsync(ResolvedLocation location)
        {
            if (!blobKeys.TryGetValue(location.BlobId, out var key))
            {
                key = await FindBlobKeyAsync(
                    store, location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId), cancellationToken)
                    .ConfigureAwait(false);
                blobKeys[location.BlobId] = key;
            }

            if (key is { } found)
            {
                needed.Add(found);
                return true;
            }

            return false;
        }

        foreach (var item in plan.Items)
        {
            if (item.Kind == EntryKind.DirectoryPlaceholder)
            {
                continue;
            }

            if (catalogue.ResolveLocation(item.ObjectId) is not { } location)
            {
                missing.Add(item.Path);
                continue;
            }

            if (!await PresentAsync(location).ConfigureAwait(false))
            {
                missing.Add(item.Path);
                continue;
            }

            // A manifest that is present but will not read is damage, not
            // absence — verify's business, and nothing this plan can name
            // segments from.
            var manifest = await ReadManifestAsync(
                store, repositoryId, keys, item.ObjectId, location, keyDeriver, objectIdDeriver, metaReaders,
                cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var references = manifest.SegmentReferences.Select(reference => reference.ObjectId)
                .Concat(manifest.Metadata.AlternateStreams.Select(stream => stream.ObjectId));
            foreach (var referenced in references)
            {
                if (catalogue.ResolveLocation(referenced) is not { } segmentLocation
                    || !await PresentAsync(segmentLocation).ConfigureAwait(false))
                {
                    missing.Add(item.Path);
                    break;
                }
            }
        }

        return new RestoreBlobSetResult(needed, missing);
    }

    /// <summary>
    /// Reads one file-version manifest through its meta blob's authenticated
    /// footer — the plan-side targeted read, cached per blob because a
    /// snapshot's manifests cluster in a few metadata blobs.
    /// </summary>
    private static async ValueTask<FileVersionManifest?> ReadManifestAsync(
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        ObjectId objectId,
        ResolvedLocation location,
        StoreBlobKeyDeriver keyDeriver,
        ObjectIdDeriver objectIdDeriver,
        MetaReaderCache metaReaders,
        CancellationToken cancellationToken)
    {
        var storeKey = BlobStoreKeys.ForBlob(
            BlobClass.Metadata, location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId));

        if (!metaReaders.TryGet(storeKey, out var cached))
        {
            BlobReader? reader;
            try
            {
                var metadata = await store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
                reader = metadata.Metadata is not { Length: > 0 }
                    ? null
                    : await BlobReader.OpenAsync(
                        store, storeKey, metadata.Metadata.Length, repositoryId,
                        keys.DeriveClassKey, objectIdDeriver, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is BlobFormatException or IOException)
            {
                reader = null;
            }

            cached = metaReaders.Add(storeKey, reader);
        }

        if (cached is null || !cached.Manifests.TryGetValue(objectId, out var located))
        {
            return null;
        }

        var read = await cached.Reader.ReadRecordAsync(located, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != RecordReadOutcome.Ok || read.Plaintext is null)
        {
            return null;
        }

        try
        {
            return FileVersionManifestCodec.Decode(read.Plaintext);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The store key a blob actually exists under, trying both classes — or
    /// null. A store that cannot answer reads as missing: the plan's job is
    /// to warn before bytes move, and "unreachable" warrants the warning as
    /// much as "absent". The found key is what the run's targeted load opens.
    /// </summary>
    private static async ValueTask<ObjectKey?> FindBlobKeyAsync(
        IObjectStore store,
        StoreBlobKey blobKey,
        CancellationToken cancellationToken)
    {
        foreach (var blobClass in new[] { BlobClass.Metadata, BlobClass.Data })
        {
            var key = BlobStoreKeys.ForBlob(blobClass, blobKey);
            try
            {
                var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
                if (metadata.Found && metadata.Metadata!.Length > 0)
                {
                    return key;
                }
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// The plan probe's open meta-blob readers: memoized, because a
    /// snapshot's manifests cluster in a few metadata blobs — and BOUNDED,
    /// because a whole-snapshot plan can touch as many metadata blobs as the
    /// repository holds, and plan memory must not scale with repository size
    /// (NFR-PERF-001). Each reader carries an object-id index over its
    /// manifest records, so the per-item lookup is a dictionary hit rather
    /// than a scan of the blob's whole record table.
    /// </summary>
    private sealed class MetaReaderCache : IDisposable
    {
        private const int Capacity = 32;

        private readonly Dictionary<ObjectKey, Cached?> _entries = [];
        private readonly Queue<ObjectKey> _openOrder = new();

        /// <summary>One open reader and its manifest index.</summary>
        public sealed record Cached(
            BlobReader Reader,
            IReadOnlyDictionary<ObjectId, RecordTableEntry> Manifests);

        /// <summary>Looks a blob up; a null <paramref name="cached"/> with a true return is a remembered unreadable blob.</summary>
        public bool TryGet(ObjectKey key, out Cached? cached) => _entries.TryGetValue(key, out cached);

        /// <summary>Caches a freshly opened reader (or the fact that the blob would not open).</summary>
        public Cached? Add(ObjectKey key, BlobReader? reader)
        {
            if (reader is null)
            {
                // Negative entries are a key and a null — never worth evicting.
                _entries[key] = null;
                return null;
            }

            if (_openOrder.Count >= Capacity)
            {
                var evicted = _openOrder.Dequeue();
                if (_entries.Remove(evicted, out var old))
                {
                    old?.Reader.Dispose();
                }
            }

            var manifests = new Dictionary<ObjectId, RecordTableEntry>();
            foreach (var entry in reader.RecordTable)
            {
                if (entry.ObjectType == ObjectType.FileVersionManifest)
                {
                    manifests.TryAdd(entry.ObjectId, entry);
                }
            }

            var cached = new Cached(reader, manifests);
            _entries[key] = cached;
            _openOrder.Enqueue(key);
            return cached;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var cached in _entries.Values)
            {
                cached?.Reader.Dispose();
            }

            _entries.Clear();
        }
    }
}
