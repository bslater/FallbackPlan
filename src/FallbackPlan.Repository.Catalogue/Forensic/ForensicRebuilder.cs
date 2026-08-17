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
    private readonly KeyHierarchy _hierarchy;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly StoreBlobKeyDeriver _storeKeyDeriver;

    /// <summary>Creates a rebuilder over the key hierarchy — footers and keys are all a scan needs (FR-MAN-007).</summary>
    public ForensicRebuilder(IObjectStore store, RepositoryId repositoryId, KeyHierarchy hierarchy)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(hierarchy);

        _store = store;
        _repositoryId = repositoryId;
        _hierarchy = hierarchy;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
        _storeKeyDeriver = new StoreBlobKeyDeriver(hierarchy.DeriveKeyIdKey());
    }

    private byte[] DeriveClassKey(BlobClass blobClass, KeyGeneration generation) =>
        blobClass == BlobClass.Data ? _hierarchy.DeriveDataKey(generation) : _hierarchy.DeriveMetadataKey(generation);

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

            foreach (var record in reader.RecordTable)
            {
                // Forensic provenance: the envelope's generation, writer, and
                // counter — deterministic, footer-derived, and below any real
                // published entry at a later generation (07 §10 reports;
                // precedence still decides).
                target.ApplyDelta(
                    DeltaId.FromBytes(System.Security.Cryptography.SHA256.HashData(
                        [.. reader.Envelope.BlobId.ToArray(), .. record.ObjectId.ToArray()]).AsSpan(0, 16)),
                    new IndexDelta
                    {
                        WriterId = reader.Envelope.WriterId,
                        Sequence = reader.Envelope.BlobCounter,
                        Generation = reader.Envelope.KeyGeneration.Value,
                        Entries =
                        [
                            new IndexEntry(
                                record.ObjectId,
                                reader.Envelope.BlobId,
                                record.PhysicalOffset,
                                record.StoredLength,
                                record.CompressionProfileValue,
                                record.EncryptionProfileValue,
                                IndexEntryType.Insertion),
                        ],
                    });

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
                var metadataKey = _hierarchy.DeriveMetadataKey(record.KeyGeneration);
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
                _hierarchy, new KeyGeneration((uint)decoded.Manifest.PublicationGeneration)))
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

        var storeKey = BlobStoreKeys.ForBlob(BlobClass.Metadata, _storeKeyDeriver.Derive(blobId));

        var metadata = await _store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
        if (!metadata.Found)
        {
            return null;
        }

        using var reader = await BlobReader.OpenAsync(
            _store, storeKey, metadata.Metadata!.Length, _repositoryId, DeriveClassKey, _objectIdDeriver, cancellationToken)
            .ConfigureAwait(false);

        var entry = reader.RecordTable.FirstOrDefault(record => record.ObjectId == objectId);
        if (entry == default)
        {
            return null;
        }

        var read = await reader.ReadRecordAsync(entry, cancellationToken).ConfigureAwait(false);
        return read.Outcome == RecordReadOutcome.Ok ? read.Plaintext : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _objectIdDeriver.Dispose();
        _storeKeyDeriver.Dispose();
    }
}
