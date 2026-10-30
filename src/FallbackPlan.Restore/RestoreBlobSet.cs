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
/// Exactly the blob set a run needs. A caller that means to open them hands
/// this to <c>RepositoryReader.LoadBlobsAsync(blobStoreKeys, …)</c>; a
/// restore does not, because it opens no blob at all (ADR-0068).
/// </param>
/// <param name="Missing">
/// Paths whose manifest or whose segments the store does not hold, reported
/// before any byte moves rather than discovered part-way through
/// (FR-RST-003). A folder's manifest is its own record, which the run reads
/// for the folder's metadata.
/// </param>
public sealed record RestoreBlobSetResult(
    IReadOnlyCollection<ObjectKey> Blobs,
    IReadOnlyList<string> Missing)
{
    /// <summary>
    /// What each item whose manifest read says of itself, by the manifest's
    /// object identifier: the bytes restoring it writes, the metadata
    /// captured with it, and the kinds of extended attribute among that a
    /// restore does not write back everywhere (FR-RST-003; ADR-0083,
    /// ADR-0087). The probe decodes every
    /// manifest to find the segments it names, so this costs it nothing
    /// more. A folder's manifest is its tree, whose head carries the folder's
    /// own metadata (ADR-0086). An item whose manifest is missing or will not
    /// read is absent.
    /// </summary>
    public IReadOnlyDictionary<ObjectId, RestoreItemFacts> Facts { get; init; } =
        new Dictionary<ObjectId, RestoreItemFacts>();

    /// <summary>
    /// What the run asks the store for (FR-RST-003; architecture 08 §2;
    /// ADR-0083 Amendment 2): each record it needs that a copy holds — every
    /// item's manifest, and a file's segments and streams — once, at the
    /// length the reader sizes a record's read by, and the envelope of each
    /// blob they sit in once. The store holds content compressed, once
    /// however many files repeat it, and not at all where a file keeps a
    /// hole, so this is seldom what the files write.
    /// </summary>
    /// <remarks>
    /// The reader's coalescing bridges a short gap between two records it
    /// wants, and reads a record again whose run it evicted before using it.
    /// The first is bounded by its bridge and the second by its budget, and
    /// a whole tree meets neither much: its records sit end to end, and the
    /// run reads them in the order they were written.
    /// </remarks>
    public ulong ReadBytes { get; init; }

    /// <summary>
    /// Paths whose manifest or content every copy that holds it holds only in
    /// an archival tier, which serves nothing until it is rehydrated
    /// (FR-RST-003; architecture 08 §2; ADR-0103). Apart from
    /// <see cref="Missing"/>: rehydrating these brings them back, and nothing
    /// brings back a path with something lost. What they need is not counted
    /// in <see cref="ReadBytes"/>, because the run cannot read it as it is.
    /// </summary>
    public IReadOnlyList<string> Archived { get; init; } = [];
}

/// <summary>
/// What a resume already holds of the files its plan names (FR-RST-003,
/// FR-RST-008; ADR-0132), so the plan counts among what its run reads only
/// what the run will read: the files its run landed at their length, of
/// which it reads only the manifest, and where its run kept the parts of the
/// files it was cut short in, of which it reads only past what each part
/// proves.
/// </summary>
public sealed record RestoreHeld
{
    /// <summary>The files the run landed at their length, by the paths the plan names them by.</summary>
    public IReadOnlySet<string> Landed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Where the run kept its parts, or null where it kept none.</summary>
    public string? PartsDirectory { get; init; }
}

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
/// This lives beside the planner rather than in the service because it is
/// the <b>plan's</b> question and not a run's. It was briefly both: when the
/// restore still opened the blobs it needed, the blob set was what a load
/// took, and lifting the probe out of the service is what let every restore
/// path stop opening every footer in the store. Since ADR-0068 a restore
/// opens no blob on the happy path, so nothing in a run asks this, and the
/// one production caller is the plan verb — which wants the other half of
/// the answer, the paths the store cannot serve, reported before any byte
/// moves (FR-RST-003).
/// </para>
/// </remarks>
public static class RestoreBlobSet
{
    /// <summary>
    /// Resolves the blob set and the unreachable paths for a plan whose run
    /// reads around what <paramref name="store"/> will not serve
    /// (FR-RST-007): a blob the store does not hold is looked for at each of
    /// <paramref name="otherCopies"/> in turn, so a path is missing only when
    /// no copy holds what it needs — the run would read it from one that does.
    /// </summary>
    public static ValueTask<RestoreBlobSetResult> ResolveAsync(
        Catalogue catalogue,
        RestorePlan plan,
        IObjectStore store,
        IReadOnlyList<CopySource> otherCopies,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        CancellationToken cancellationToken) =>
        ResolveAsync(catalogue, plan, store, otherCopies, repositoryId, keys, held: null, cancellationToken);

    /// <summary>
    /// Resolves the blob set and the unreachable paths for a plan of a
    /// resume whose run reads around what <paramref name="store"/> will not
    /// serve, counting among what it reads nothing <paramref name="held"/>
    /// says the run already holds.
    /// </summary>
    public static ValueTask<RestoreBlobSetResult> ResolveAsync(
        Catalogue catalogue,
        RestorePlan plan,
        IObjectStore store,
        IReadOnlyList<CopySource> otherCopies,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        RestoreHeld? held,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(otherCopies);
        return ResolveAsync(
            catalogue, plan, otherCopies.Count == 0 ? store : new FirstHolderStore(store, otherCopies),
            repositoryId, keys, held, cancellationToken);
    }

    /// <summary>Resolves the blob set and the unreachable paths for <paramref name="plan"/>.</summary>
    public static ValueTask<RestoreBlobSetResult> ResolveAsync(
        Catalogue catalogue,
        RestorePlan plan,
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        CancellationToken cancellationToken) =>
        ResolveAsync(catalogue, plan, store, repositoryId, keys, held: null, cancellationToken);

    /// <summary>
    /// Resolves the blob set and the unreachable paths for a plan of a
    /// resume, counting among what it reads nothing <paramref name="held"/>
    /// says the run already holds (ADR-0132). What is held is still probed:
    /// a path stays missing or archived by everything it names, because a
    /// file found landed at its length may still differ, and is then
    /// restored.
    /// </summary>
    public static async ValueTask<RestoreBlobSetResult> ResolveAsync(
        Catalogue catalogue,
        RestorePlan plan,
        IObjectStore store,
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        RestoreHeld? held,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);

        using var keyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        using var objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        using var metaReaders = new MetaReaderCache();
        var blobKeys = new Dictionary<BlobId, HeldBlob?>();
        var missing = new List<string>();
        var archived = new List<string>();
        var needed = new HashSet<ObjectKey>();
        var facts = new Dictionary<ObjectId, RestoreItemFacts>();
        var reads = new HashSet<ObjectId>();
        var readBytes = 0UL;

        async ValueTask<Holding> HoldingAsync(ObjectId objectId, ResolvedLocation location, bool counted = true)
        {
            if (!blobKeys.TryGetValue(location.BlobId, out var blob))
            {
                blob = await FindBlobKeyAsync(
                    store, location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId), cancellationToken)
                    .ConfigureAwait(false);
                blobKeys[location.BlobId] = blob;
            }

            if (blob is not { } found)
            {
                return Holding.Absent;
            }

            if (counted && needed.Add(found.Key) && !found.Archived)
            {
                readBytes += (ulong)BlobReader.EnvelopeReadLength(found.Length);
            }

            if (found.Archived)
            {
                return Holding.Archived;
            }

            if (counted && reads.Add(objectId))
            {
                readBytes += (ulong)RecordFraming.MaxRecordLength(location.StoredLength);
            }

            return Holding.Readable;
        }

        foreach (var item in plan.Items)
        {
            if (catalogue.ResolveLocation(item.ObjectId) is not { } location)
            {
                missing.Add(item.Path);
                continue;
            }

            switch (await HoldingAsync(item.ObjectId, location).ConfigureAwait(false))
            {
                case Holding.Absent:
                    missing.Add(item.Path);
                    continue;

                case Holding.Archived:
                    archived.Add(item.Path);
                    continue;
            }

            // A manifest that is present but will not read is damage, not
            // absence — verify's business, and nothing this plan can name
            // segments from.
            var record = await ReadManifestAsync(
                store, repositoryId, keys, item.ObjectId, location, keyDeriver, objectIdDeriver, metaReaders,
                cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            // A folder's manifest is its tree, whose head carries what was
            // captured with the folder itself. It names no segments.
            if (item.Kind == EntryKind.DirectoryPlaceholder)
            {
                if (Decode(record, TreeManifestCodec.Decode)?.Metadata is { } own)
                {
                    facts[item.ObjectId] = new RestoreItemFacts(
                        EntryKind.DirectoryPlaceholder, 0, MetadataRules.Captured(own),
                        own.OwnerName, own.GroupName, own.PosixMode, MetadataRules.KindsOf(own),
                        Accounts: MetadataRules.AccountsOf(own), FileAttributes: own.FileAttributes);
                }

                continue;
            }

            if (Decode(record, FileVersionManifestCodec.Decode) is not { } manifest)
            {
                continue;
            }

            // What the run already holds of the file is not read again: a
            // file it landed at its length but for its manifest, and a file
            // it was cut short in as far as its part proves, in the file's
            // order (ADR-0132). Nor does what a part holds need room in the
            // working space again (ADR-0136).
            var landed = held is not null && held.Landed.Contains(item.Path);
            var heldSegments = landed
                ? manifest.SegmentReferences.Count
                : held?.PartsDirectory is { } parts
                    ? await RestorePart.HeldSegmentsAsync(
                        Path.Combine(parts, RestorePart.NameOf(manifest)),
                        manifest,
                        (reference, plaintext) => RestorePart.Proves(objectIdDeriver, reference, plaintext),
                        cancellationToken).ConfigureAwait(false)
                    : 0;

            facts[item.ObjectId] = new RestoreItemFacts(
                manifest.EntryKind, RestoreSpace.WrittenBytes(manifest), MetadataRules.Captured(manifest.Metadata),
                manifest.Metadata.OwnerName, manifest.Metadata.GroupName, manifest.Metadata.PosixMode,
                MetadataRules.KindsOf(manifest.Metadata), HardLinkGroups.KeyOf(manifest),
                RestoreSpace.HeldBytes(manifest, heldSegments), MetadataRules.AccountsOf(manifest.Metadata),
                manifest.Metadata.FileAttributes);

            // A lost segment makes the path missing whatever else of it is
            // archived, because no rehydration brings that segment back. The
            // codec holds a manifest's segments in the file's order.
            var references = manifest.SegmentReferences
                .Select((reference, index) => (reference.ObjectId, Counted: index >= heldSegments))
                .Concat(manifest.Metadata.AlternateStreams.Select(stream => (stream.ObjectId, Counted: !landed)));
            var archivedSegment = false;
            var lost = false;
            foreach (var (referenced, counted) in references)
            {
                var holding = catalogue.ResolveLocation(referenced) is { } segmentLocation
                    ? await HoldingAsync(referenced, segmentLocation, counted).ConfigureAwait(false)
                    : Holding.Absent;
                if (holding == Holding.Absent)
                {
                    lost = true;
                    break;
                }

                archivedSegment |= holding == Holding.Archived;
            }

            if (lost)
            {
                missing.Add(item.Path);
            }
            else if (archivedSegment)
            {
                archived.Add(item.Path);
            }
        }

        return new RestoreBlobSetResult(needed, missing) { Facts = facts, ReadBytes = readBytes, Archived = archived };
    }

    /// <summary>
    /// Reads one manifest's record — a file version's, or a folder's tree —
    /// through its meta blob's authenticated footer: the plan-side targeted
    /// read, cached per blob because a snapshot's manifests cluster in a few
    /// metadata blobs.
    /// </summary>
    private static async ValueTask<byte[]?> ReadManifestAsync(
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
        return read.Outcome == RecordReadOutcome.Ok ? read.Plaintext : null;
    }

    /// <summary>A manifest record decoded, or null where it will not decode.</summary>
    private static T? Decode<T>(byte[] record, Func<ReadOnlyMemory<byte>, T> decode)
        where T : class
    {
        try
        {
            return decode(record);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The store key a blob actually exists under, trying both classes, its
    /// length, and whether it is held only in an archival tier — or null. A
    /// store that cannot answer reads as missing: the plan's job is to warn
    /// before bytes move, and "unreachable" warrants the warning as much as
    /// "absent". The found key is what the run's targeted load opens.
    /// </summary>
    private static async ValueTask<HeldBlob?> FindBlobKeyAsync(
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
                    return new HeldBlob(key, metadata.Metadata.Length, metadata.Metadata.Archived);
                }
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    /// <summary>Where a record the plan needs stands at the copies.</summary>
    private enum Holding
    {
        /// <summary>No copy holds its blob.</summary>
        Absent,

        /// <summary>Every copy that holds its blob holds it only in an archival tier.</summary>
        Archived,

        /// <summary>A copy holds its blob where it can be read.</summary>
        Readable,
    }

    /// <summary>A blob a copy holds: the key it is under, its length, and whether that copy holds it archived.</summary>
    private sealed record HeldBlob(ObjectKey Key, long Length, bool Archived);

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
                if (entry.ObjectType is ObjectType.FileVersionManifest or ObjectType.TreeManifest)
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
