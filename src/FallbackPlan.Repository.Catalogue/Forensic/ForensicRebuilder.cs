using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Catalogue.Forensic;

/// <summary>What a forensic scan is asked to recover — a scan MUST accept a target (specification 07 §10).</summary>
public abstract record ForensicTarget
{
    private ForensicTarget()
    {
    }

    /// <summary>The whole repository.</summary>
    public sealed record Everything : ForensicTarget;

    /// <summary>A named set of object identifiers.</summary>
    public sealed record Objects(IReadOnlySet<ObjectId> Ids) : ForensicTarget;

    /// <summary>One snapshot by its 16-byte identity.</summary>
    public sealed record Snapshot(ReadOnlyMemory<byte> SnapshotId) : ForensicTarget;
}

/// <summary>The outcome of a forensic rebuild — a report, never a repair (07 §10).</summary>
public sealed record ForensicReport(
    int MetadataBlobsScanned,
    int DataBlobsScanned,
    int RecordsIndexed,
    bool TargetSatisfied,
    IReadOnlyList<DamageFinding> Findings);

/// <summary>
/// Rebuilds the index-free way (specification 07 §10; E2/E3; FR-MAN-009,
/// FR-MAN-010, FR-MAN-014; NFR-PERF-015): every recovery footer is
/// self-contained, snapshots enumerate from a bounded prefix, and the scan
/// is <b>targeted</b> — metadata blobs first (the small class, and where the
/// graph lives), then data blobs only until the target's dependency set is
/// located. Rebuild reports and never repairs: the scan issues no put and no
/// delete, and conflicting mappings are retained as findings.
/// </summary>
public sealed class ForensicRebuilder : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly RepositoryWriteCredential _credential;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly StoreBlobKeyDeriver _storeKeyDeriver;

    // The target walk reads one metadata record at a time, and every read
    // needs the containing blob's record table to find the record's offset.
    // Opening a reader per record re-issues three range reads and re-decrypts
    // and re-decodes a table that can hold 65 536 entries — so a walk over a
    // blob's R records cost 4R range reads and O(R^2) table scanning, against
    // NFR-PERF-012's rate and NFR-PERF-015's time to first restored file.
    //
    // One blob is cached rather than many, because a footer's table can reach
    // 16 MiB and NFR-PERF-001 bounds memory by configured limits and not by
    // repository size. One is enough: manifests written together land in the
    // same blob and the walk descends the tree in the order it was written,
    // so consecutive reads hit the same blob nearly always. The index beside
    // it turns the table lookup from a scan into a probe.
    private BlobId? _cachedBlobId;
    private BlobReader? _cachedReader;
    private Dictionary<ObjectId, RecordTableEntry>? _cachedTable;

    /// <summary>Creates a rebuilder over the write credential — footers and keys are all a scan needs (FR-MAN-007).</summary>
    public ForensicRebuilder(IObjectStore store, RepositoryId repositoryId, RepositoryWriteCredential credential)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);

        _store = store;
        _repositoryId = repositoryId;
        _credential = credential;
        _objectIdDeriver = new ObjectIdDeriver(credential.ContentIdKey.ToArray());
        _storeKeyDeriver = new StoreBlobKeyDeriver(credential.KeyIdKey.ToArray());
    }

    // The reader asks for a blob's STRUCTURE class, which is the metadata
    // plane for every blob — a sealed data blob's footer derives from the
    // metadata key too (ADR-0042 §2). There is no data key to hand out.
    private byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) =>
        blobClass == BlobClass.Metadata
            ? _credential.DeriveMetadataKey(generation)
            : throw new InvalidOperationException("A repository holds no data class key (specification 03 §9.2).");

    /// <summary>Runs the scan into <paramref name="target"/>.</summary>
    public async ValueTask<ForensicReport> RebuildAsync(
        Catalogue target,
        ForensicTarget scope,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(target);
        ThrowHelper.ThrowIfNull(scope);

        var findings = new List<DamageFinding>();
        var indexed = new Dictionary<ObjectId, BlobId>();
        var recordsIndexed = 0;

        // Phase 1: metadata blobs — the bounded class carrying the whole
        // graph. Always scanned in full; it is small by construction.
        var metadataBlobs = 0;
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("blobs/meta/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            metadataBlobs++;
            recordsIndexed += await ScanBlobAsync(target, entry, indexed, findings, cancellationToken).ConfigureAwait(false);
        }

        // Resolve the target's needed object identifiers from the graph.
        var needed = await ResolveTargetAsync(target, scope, indexed, findings, cancellationToken).ConfigureAwait(false);
        needed.ExceptWith(indexed.Keys);

        // Phase 2: data blobs — ordered scanning that stops the moment the
        // target's dependency set is located (07 §10's targeting rule;
        // restore becomes possible without waiting for the full repository).
        var dataBlobs = 0;
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (scope is not ForensicTarget.Everything && needed.Count == 0)
            {
                break;
            }

            dataBlobs++;
            recordsIndexed += await ScanBlobAsync(target, entry, indexed, findings, cancellationToken).ConfigureAwait(false);
            needed.ExceptWith(indexed.Keys);
        }

        // Anything still needed after the scan is genuinely missing (01 §5:
        // a missing referenced object is a damage finding, never an inferred
        // invalid reference).
        foreach (var missing in needed)
        {
            var finding = new DamageFinding(
                DamageKind.MissingBlob,
                $"Object {missing} is referenced by the target but no scanned blob carries it (specification 01 §5).");
            findings.Add(finding);
            target.RecordFinding(finding, missing);
        }

        target.SetSource("forensic-rebuild");

        return new ForensicReport(metadataBlobs, dataBlobs, recordsIndexed, needed.Count == 0, findings);
    }

    private async ValueTask<int> ScanBlobAsync(
        Catalogue target,
        ObjectEntry entry,
        Dictionary<ObjectId, BlobId> indexed,
        List<DamageFinding> findings,
        CancellationToken cancellationToken)
    {
        BlobReader reader;
        try
        {
            reader = await BlobReader.OpenAsync(
                _store, entry.Key, entry.Length, _repositoryId, DeriveClassKey, _objectIdDeriver, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BlobFormatException exception)
        {
            // A damaged container is a finding, scoped to the blob — the
            // scan continues; corruption is local (04 §7).
            var finding = new DamageFinding(DamageKind.CorruptRecord, $"Blob object '{entry.Key}': {exception.Message}");
            findings.Add(finding);
            target.RecordFinding(finding);
            return 0;
        }

        using (reader)
        {
            target.RecordBlob(
                reader.Envelope.BlobId,
                _storeKeyDeriver.Derive(reader.Envelope.BlobId),
                reader.Envelope.BlobClass,
                reader.Envelope.KeyGeneration,
                reader.RecordTable.Count,
                entry.Length,
                digest: default);

            // Forensic provenance: the envelope's generation, writer and
            // counter — deterministic, footer-derived, and below any real
            // published entry at a later generation (07 §10 reports;
            // precedence still decides).
            //
            // One delta for the whole blob, not one per record. The
            // catalogue's delta ledger is UNIQUE (writer_id, sequence) and
            // every record of a blob shares the blob's counter, so a delta
            // per record would be taken for a re-application of the first
            // and every record after it would be dropped — a rebuild that
            // reported a complete index holding one record per blob.
            target.ApplyDelta(
                DeltaId.FromBytes(System.Security.Cryptography.SHA256.HashData(
                    reader.Envelope.BlobId.ToArray()).AsSpan(0, 16)),
                new IndexDelta
                {
                    WriterId = reader.Envelope.WriterId,
                    Sequence = reader.Envelope.BlobCounter,
                    Generation = reader.Envelope.KeyGeneration.Value,
                    Entries =
                    [
                        .. reader.RecordTable.Select(record => new IndexEntry(
                            record.ObjectId,
                            reader.Envelope.BlobId,
                            record.PhysicalOffset,
                            record.StoredLength,
                            record.CompressionProfileValue,
                            record.EncryptionProfileValue,
                            IndexEntryType.Insertion)),
                    ],
                });

            foreach (var record in reader.RecordTable)
            {
                indexed.TryAdd(record.ObjectId, reader.Envelope.BlobId);
            }

            return reader.RecordTable.Count;
        }
    }

    /// <summary>
    /// Resolves the object identifiers the target needs, walking snapshots →
    /// trees → file versions → segments through the already-indexed metadata
    /// records.
    /// </summary>
    private async ValueTask<HashSet<ObjectId>> ResolveTargetAsync(
        Catalogue catalogue,
        ForensicTarget scope,
        Dictionary<ObjectId, BlobId> indexed,
        List<DamageFinding> findings,
        CancellationToken cancellationToken)
    {
        if (scope is ForensicTarget.Objects objects)
        {
            return [.. objects.Ids];
        }

        var needed = new HashSet<ObjectId>();

        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            byte[] bytes;
            using (var read = await _store.OpenReadAsync(entry.Key, range: null, cancellationToken).ConfigureAwait(false))
            {
                if (read.Outcome != OpenReadOutcome.Found)
                {
                    continue;
                }

                using var memory = new MemoryStream();
                await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                bytes = memory.ToArray();
            }

            DecodedSnapshot decoded;
            try
            {
                var record = StandaloneRecordFraming.Parse(bytes);
                var metadataKey = _credential.DeriveMetadataKey(record.KeyGeneration);
                try
                {
                    if (!StandaloneRecordCipher.TryOpen(record, _repositoryId, metadataKey, out var plaintext))
                    {
                        var finding = new DamageFinding(
                            DamageKind.CorruptRecord, $"Snapshot object '{entry.Key}' failed authentication.");
                        findings.Add(finding);
                        catalogue.RecordFinding(finding);
                        continue;
                    }

                    decoded = SnapshotManifestCodec.Decode(plaintext);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadataKey);
                }
            }
            catch (FormatException exception)
            {
                var finding = new DamageFinding(
                    DamageKind.CorruptRecord, $"Snapshot object '{entry.Key}': {exception.Message}");
                findings.Add(finding);
                catalogue.RecordFinding(finding);
                continue;
            }

            if (scope is ForensicTarget.Snapshot snapshot &&
                !decoded.Manifest.SnapshotId.Span.SequenceEqual(snapshot.SnapshotId.Span))
            {
                continue;
            }

            // Project the snapshot row — with the signature's verdict — so
            // the rebuilt catalogue answers `snapshots` (schema v2).
            int signatureState;
            using (var signer = RepositorySigner.Create(
                _credential, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
            {
                signatureState = signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span) ? 1 : 2;
            }

            catalogue.RecordSnapshot(
                decoded.Manifest.SnapshotId.Span,
                decoded.Manifest.DeviceId.Span,
                decoded.Manifest.BackupSetId.Span,
                _objectIdDeriver.Derive(ObjectType.SnapshotManifest, ContentHasher.Hash(
                    SnapshotManifestCodec.Encode(decoded.Manifest, decoded.Signature.Span))),
                decoded.Manifest.RootTree,
                decoded.Manifest.PublicationGeneration,
                decoded.Manifest.CaptureStatus,
                signatureState,
                decoded.Manifest.CaptureCompletedAt);

            // Walk root tree → subdirectories → file versions → segments
            // through the indexed metadata records, projecting the paths.
            await WalkTreeAsync(
                decoded.Manifest.SnapshotId, decoded.Manifest.RootTree, prefix: string.Empty,
                indexed, needed, findings, catalogue, cancellationToken)
                .ConfigureAwait(false);
        }

        return needed;
    }

    private async ValueTask WalkTreeAsync(
        ReadOnlyMemory<byte> snapshotId,
        ObjectId treeId,
        string prefix,
        Dictionary<ObjectId, BlobId> indexed,
        HashSet<ObjectId> needed,
        List<DamageFinding> findings,
        Catalogue catalogue,
        CancellationToken cancellationToken)
    {
        var plaintext = await ReadMetadataRecordAsync(treeId, indexed, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            var finding = new DamageFinding(
                DamageKind.MissingIndexObject, $"The tree manifest {treeId} is not present in any metadata blob.");
            findings.Add(finding);
            catalogue.RecordFinding(finding, treeId);
            return;
        }

        var tree = TreeManifestCodec.Decode(plaintext);

        foreach (var entry in tree.Entries)
        {
            var name = System.Text.Encoding.UTF8.GetString(entry.Name.Span);
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            catalogue.RecordTreeEntry(snapshotId.Span, path, entry.EntryKind, entry.ObjectId);

            // A subdirectory entry names its child tree manifest
            // (ADR-0026 §Decision 6) — the walk descends it.
            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                await WalkTreeAsync(snapshotId, entry.ObjectId, path, indexed, needed, findings, catalogue, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var manifestBytes = await ReadMetadataRecordAsync(entry.ObjectId, indexed, cancellationToken).ConfigureAwait(false);
            if (manifestBytes is null)
            {
                var finding = new DamageFinding(
                    DamageKind.MissingIndexObject, $"File version {entry.ObjectId} is not present in any metadata blob.");
                findings.Add(finding);
                catalogue.RecordFinding(finding, entry.ObjectId);
                continue;
            }

            var manifest = FileVersionManifestCodec.Decode(manifestBytes);
            catalogue.RecordFileVersion(
                entry.ObjectId,
                manifest.Name.Span,
                manifest.EntryKind,
                manifest.LogicalLength,
                manifest.WholeFileHash.Span,
                manifest.ParentVersion,
                manifest.SegmentReferences.Count,
                manifest.Metadata.ModifiedAt,
                hasAlternateStreams: manifest.Metadata.AlternateStreams.Count > 0,
                metadataDigest: FileVersionManifestCodec.MetadataDigest(manifest.Metadata));

            foreach (var reference in manifest.SegmentReferences)
            {
                needed.Add(reference.ObjectId);
            }

            foreach (var stream in manifest.Metadata.AlternateStreams)
            {
                needed.Add(stream.ObjectId);
            }
        }

        if (tree.Continuation is { } continuation)
        {
            await WalkTreeAsync(snapshotId, continuation, prefix, indexed, needed, findings, catalogue, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]?> ReadMetadataRecordAsync(
        ObjectId objectId,
        Dictionary<ObjectId, BlobId> indexed,
        CancellationToken cancellationToken)
    {
        if (!indexed.TryGetValue(objectId, out var blobId))
        {
            return null;
        }

        if (!await EnsureCachedAsync(blobId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!_cachedTable!.TryGetValue(objectId, out var entry))
        {
            return null;
        }

        var read = await _cachedReader!.ReadRecordAsync(entry, cancellationToken).ConfigureAwait(false);
        return read.Outcome == RecordReadOutcome.Ok ? read.Plaintext : null;
    }

    /// <summary>
    /// Makes <paramref name="blobId"/> the cached blob, answering false when
    /// it cannot be opened. A blob that fails to open is not a finding here:
    /// the scan has already opened every blob it listed and recorded whatever
    /// damage it found, so a failure at this point means the object went away
    /// under us and the record is simply unreachable.
    /// </summary>
    private async ValueTask<bool> EnsureCachedAsync(BlobId blobId, CancellationToken cancellationToken)
    {
        if (_cachedBlobId is { } cached && cached.Equals(blobId))
        {
            return true;
        }

        ReleaseCached();

        var storeKey = BlobStoreKeys.ForBlob(BlobClass.Metadata, _storeKeyDeriver.Derive(blobId));

        var metadata = await _store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
        if (!metadata.Found)
        {
            return false;
        }

        // A blob reached here was opened by the scan already, so a format
        // failure now is not the local corruption ScanBlobAsync records as a
        // finding — it is the object changing underneath a read-only pass.
        // It propagates, as it did before this blob was held across records.
        var reader = await BlobReader.OpenAsync(
            _store, storeKey, metadata.Metadata!.Length, _repositoryId, DeriveClassKey, _objectIdDeriver,
            cancellationToken)
            .ConfigureAwait(false);

        var table = new Dictionary<ObjectId, RecordTableEntry>(reader.RecordTable.Count);
        foreach (var record in reader.RecordTable)
        {
            table.TryAdd(record.ObjectId, record);
        }

        _cachedBlobId = blobId;
        _cachedReader = reader;
        _cachedTable = table;
        return true;
    }

    private void ReleaseCached()
    {
        _cachedReader?.Dispose();
        _cachedBlobId = null;
        _cachedReader = null;
        _cachedTable = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ReleaseCached();
        _objectIdDeriver.Dispose();
        _storeKeyDeriver.Dispose();
    }
}
