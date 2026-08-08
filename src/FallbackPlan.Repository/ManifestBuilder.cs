using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Bridges archive results to the manifest graph (specification 06;
/// FR-MAN-003) and writes manifests as records into metadata-class blobs
/// (blob_class 0x0002) — individually addressable records like any other,
/// with their own encryption, tags, and footer entries (06 §1).
/// </summary>
public sealed class ManifestBuilder : IAsyncDisposable
{
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyGeneration _generation;
    private readonly RepositoryKeySet _keys;
    private readonly IObjectStore _store;
    private readonly IBlobCounterAllocator _counters;
    private readonly string _spoolDirectory;
    private readonly BlobWriteProfile _blobProfile;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly StoreBlobKeyDeriver _storeKeyDeriver;
    private readonly byte[] _metadataClassKey;
    private readonly List<ArchivedBlob> _blobs = [];
    private readonly IIntentScope? _intentScope;
    private readonly ReusePredicate? _mayReuse;
    private BlobWriter? _writer;

    /// <summary>Creates a builder writing metadata blobs under <paramref name="blobProfile"/>.</summary>
    public ManifestBuilder(
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration generation,
        RepositoryKeySet keys,
        IObjectStore store,
        IBlobCounterAllocator counters,
        string spoolDirectory,
        BlobWriteProfile blobProfile,
        IIntentScope? intentScope = null,
        ReusePredicate? mayReuse = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(blobProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);

        _repositoryId = repositoryId;
        _writerId = writerId;
        _generation = generation;
        _keys = keys;
        _store = store;
        _counters = counters;
        _spoolDirectory = spoolDirectory;
        _blobProfile = blobProfile;
        _intentScope = intentScope;
        _mayReuse = mayReuse;
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        _storeKeyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        _metadataClassKey = keys.DeriveClassKey(BlobClass.Metadata, generation);
    }

    /// <summary>The metadata blobs sealed and uploaded so far.</summary>
    public IReadOnlyList<ArchivedBlob> Blobs => _blobs;

    /// <summary>
    /// Builds the file-version manifest for an archive result: the logical
    /// references verbatim, the whole-file hash, and the segmentation
    /// profile — and nothing physical, structurally (06 §3.1).
    /// </summary>
    public static FileVersionManifest FromArchiveResult(
        ArchiveResult result,
        ReadOnlyMemory<byte> name,
        NameNormalisation nameNormalisation,
        EntryMetadata metadata,
        ObjectId? parentVersion,
        IReadOnlyList<string>? captureDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(metadata);

        return new FileVersionManifest
        {
            EntryKind = EntryKind.File,
            Name = name,
            NameNormalisation = nameNormalisation,
            LogicalLength = (ulong)result.LogicalLength,
            SegmentReferences = result.SegmentReferences,
            WholeFileHash = result.WholeFileHash.ToArray(),
            SegmentationProfile = result.SegmentationProfile.Value,
            ParentVersion = parentVersion,
            Metadata = metadata,
            CaptureDiagnostics = captureDiagnostics ?? [],
        };
    }

    /// <summary>
    /// Appends one encoded manifest as a metadata record, returning its
    /// object identifier (<c>HMAC(content_id_key, type ‖ SHA-256(bytes))</c>,
    /// specification 02 §3). Blobs rotate at the profile's targets exactly
    /// as data blobs do.
    /// </summary>
    /// <remarks>
    /// A manifest whose object the index already locates is **not** written
    /// again; its identifier is returned and the reference resolves to what
    /// is already there. This is the same reuse the segment path performs,
    /// and it is what keeps metadata growth incremental (NFR-PERF-005): a
    /// directory whose contents did not change encodes to the same bytes and
    /// therefore the same object, so re-emitting it would make every
    /// snapshot rewrite every tree in the repository — a cost proportional
    /// to the whole repository for a backup that changed one file.
    /// <para>
    /// It is the same gate too, not merely the same idea: referencing another
    /// writer's manifest is the trust question referencing their segment is
    /// (ADR-0006), so the configured domain decides both. Under the default a
    /// manifest this writer did not store is confirmed before it is
    /// referenced, and under <c>device</c> it is written again.
    /// </para>
    /// </remarks>
    public async ValueTask<ObjectId> AppendManifestAsync(
        ObjectType objectType,
        ReadOnlyMemory<byte> encodedManifest,
        CancellationToken cancellationToken)
    {
        var contentId = ContentHasher.Hash(encodedManifest.Span);
        var objectId = _objectIdDeriver.Derive(objectType, contentId);

        if (_mayReuse is not null &&
            await _mayReuse(objectId, cancellationToken).ConfigureAwait(false))
        {
            return objectId;
        }

        if (_writer is not null && !_writer.CanAppend(encodedManifest.Length))
        {
            await SealAndUploadAsync(cancellationToken).ConfigureAwait(false);
        }

        _writer ??= BlobWriter.Create(
            _repositoryId,
            _writerId,
            _generation,
            BlobClass.Metadata,
            _metadataClassKey,
            _counters.AllocateNext(),
            EncryptionProfile.Aes256GcmV1,
            _blobProfile,
            _spoolDirectory);

        await _writer.AppendRecordAsync(
            objectType,
            objectId,
            CompressionProfile.None,
            (ulong)encodedManifest.Length,
            encodedManifest,
            cancellationToken).ConfigureAwait(false);

        if (_writer.ShouldSeal)
        {
            await SealAndUploadAsync(cancellationToken).ConfigureAwait(false);
        }

        return objectId;
    }

    /// <summary>Seals and uploads the open metadata blob, if any.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            await SealAndUploadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the snapshot's standalone copy at
    /// <c>/snapshots/&lt;device&gt;/&lt;set&gt;/&lt;snapshot&gt;</c>
    /// (specification 06 §6), sealed under the FBPKSREC framing
    /// (ADR-0022 §Decision 1) with the same manifest bytes — and therefore
    /// the same object identifier — as the in-blob record.
    /// </summary>
    public async ValueTask WriteStandaloneSnapshotAsync(
        SnapshotManifest manifest,
        ReadOnlyMemory<byte> encodedManifest,
        ulong counter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var contentId = ContentHasher.Hash(encodedManifest.Span);
        var objectId = _objectIdDeriver.Derive(ObjectType.SnapshotManifest, contentId);

        var sealedObject = StandaloneRecordCipher.Seal(
            _repositoryId,
            _metadataClassKey,
            _generation,
            _writerId,
            counter,
            ObjectType.SnapshotManifest,
            objectId,
            encodedManifest.Span);

        var key = MetadataStoreKeys.Snapshot(
            manifest.DeviceId.Span, manifest.BackupSetId.Span, manifest.SnapshotId.Span);

        var put = await _store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        if (put.Outcome == PutOutcome.PreconditionFailed)
        {
            throw new IOException($"The store refused snapshot object '{key}' with a failed precondition.");
        }
    }

    /// <summary>
    /// Writes one source-identity hint per newly created file version
    /// (specification 06 §11), sealed under the same standalone framing as
    /// the snapshot object.
    /// </summary>
    /// <param name="hints">The hints to publish; an empty list writes nothing.</param>
    /// <param name="intentSequence">
    /// The publication's write-intent sequence number, which every hint
    /// carries.
    /// </param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <remarks>
    /// <para>
    /// Advisory, so a store that refuses a put is not a publication failure —
    /// a later reader simply falls back to matching by path and misses that
    /// file's rename. They are written <em>before</em> the snapshot object
    /// for the ordinary reason: a hint that becomes visible after the
    /// snapshot that needs it is a hint that is missing exactly when it is
    /// wanted.
    /// </para>
    /// <para>
    /// Every hint of one publication carries the intent's sequence number
    /// rather than drawing its own. A number per hint would be a durable
    /// state write per changed file and an accounting obligation per changed
    /// file, and hints discharge none of the four (ADR-0022 §Decision 7);
    /// the intent's number is already accounted by its journal record. Key
    /// uniqueness never rested on the counter — each object seals under a
    /// fresh 32-byte salt.
    /// </para>
    /// </remarks>
    public async ValueTask WriteSourceIdentityHintsAsync(
        IReadOnlyList<SourceIdentityHint> hints,
        ulong intentSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hints);

        foreach (var hint in hints)
        {
            var encoded = SourceIdentityHintCodec.Encode(hint);
            var contentId = ContentHasher.Hash(encoded);
            var objectId = _objectIdDeriver.Derive(ObjectType.SourceIdentityHint, contentId);

            var sealedObject = StandaloneRecordCipher.Seal(
                _repositoryId,
                _metadataClassKey,
                _generation,
                _writerId,
                intentSequence,
                ObjectType.SourceIdentityHint,
                objectId,
                encoded);

            await _store.PutAsync(
                MetadataStoreKeys.SourceIdentityHint(hint.SourceKey.Span, hint.CapturedAt, hint.SnapshotId.Span),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
                PutConditions.IfNotExists,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SealAndUploadAsync(CancellationToken cancellationToken)
    {
        var sealedBlob = await _writer!.SealAsync(cancellationToken).ConfigureAwait(false);
        await using (sealedBlob.ConfigureAwait(false))
        {
            var storeBlobKey = _storeKeyDeriver.Derive(sealedBlob.BlobId);
            var storeKey = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeBlobKey);

            // Metadata blobs are blobs: the 08 §3.1 rule has no exception
            // for them.
            if (_intentScope is not null)
            {
                await _intentScope.EnsureCoveredAsync(sealedBlob.BlobId, cancellationToken).ConfigureAwait(false);
            }

            var put = await _store.PutAsync(storeKey, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, cancellationToken)
                .ConfigureAwait(false);

            if (put.Outcome == PutOutcome.PreconditionFailed)
            {
                throw new IOException($"The store refused blob '{storeKey}' with a failed precondition.");
            }

            FallbackPlan.Domain.Diagnostics.EngineDiagnostics.BlobsSealed.Add(
                1, new KeyValuePair<string, object?>("class", "meta"));
            FallbackPlan.Domain.Diagnostics.EngineDiagnostics.BlobFillFraction.Record(
                sealedBlob.Length / (double)_blobProfile.TargetSizeBytes);

            _blobs.Add(new ArchivedBlob(
                sealedBlob.BlobId,
                storeBlobKey,
                storeKey,
                sealedBlob.Digest,
                sealedBlob.RecordTable.Count,
                sealedBlob.Length,
                sealedBlob.RecordTable));
        }

        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        _objectIdDeriver.Dispose();
        _storeKeyDeriver.Dispose();
        CryptographicOperations.ZeroMemory(_metadataClassKey);
    }
}
