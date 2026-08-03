using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// A durable-intent guard over blob uploads (specification 08 §3.1, §4):
/// before a blob's bytes leave the process, the scope guarantees an
/// unretired intent naming that blob is durable in the store.
/// </summary>
public interface IIntentScope
{
    /// <summary>Ensures a durable intent names <paramref name="blobId"/> before returning.</summary>
    ValueTask EnsureCoveredAsync(BlobId blobId, CancellationToken cancellationToken);
}

/// <summary>The nine steps of the canonical publication order (specification 08 §10).</summary>
public enum PublicationStep
{
    /// <summary>1 — the write intent is durable.</summary>
    PublishIntent = 1,

    /// <summary>2 — the source is scanned.</summary>
    ScanSource = 2,

    /// <summary>3 — segmentation, comparison, compression, encryption, assembly.</summary>
    SegmentAndSeal = 3,

    /// <summary>4 — blobs sealed and uploaded.</summary>
    UploadBlobs = 4,

    /// <summary>5 — acknowledgements verified.</summary>
    VerifyAcknowledgements = 5,

    /// <summary>6 — index deltas published, referencing now-durable blobs.</summary>
    PublishIndexDeltas = 6,

    /// <summary>7 — the signed snapshot published, referencing an already-published root tree.</summary>
    PublishSnapshot = 7,

    /// <summary>8 — the intent retired.</summary>
    RetireIntent = 8,

    /// <summary>9 — the local job marked complete.</summary>
    Complete = 9,
}

/// <summary>
/// Observes publication steps — the F1 interruption harness's kill points: an
/// observer that throws after step <em>n</em> is a crash between <em>n</em>
/// and <em>n</em>+1.
/// </summary>
public interface IPublicationObserver
{
    /// <summary>Called after each step's effects are durable.</summary>
    void AfterStep(PublicationStep completedStep);
}

/// <summary>One publication job: a single file version in phase 0.</summary>
/// <remarks>
/// <see cref="CaptureDiagnostics"/> lands in the file-version manifest's
/// key 13 — the one legitimate home for import provenance (FR-CP-002): an
/// imported version is otherwise format-indistinguishable from a native one.
/// </remarks>
public sealed record BackupJob(
    Stream Source,
    ReadOnlyMemory<byte> FileName,
    ReadOnlyMemory<byte> DeviceId,
    ReadOnlyMemory<byte> BackupSetId,
    ReadOnlyMemory<byte> SnapshotId,
    ulong NowUnixMilliseconds,
    ulong DeclaredMaxDurationMs,
    ulong ExpiryGeneration,
    string ClientVersion,
    IReadOnlyList<string>? CaptureDiagnostics = null);

/// <summary>The published outcome.</summary>
public sealed record PublishedSnapshot(
    ObjectId SnapshotObjectId,
    ObjectId FileVersionObjectId,
    ObjectId RootTreeObjectId,
    ObjectId PolicyObjectId,
    DeltaId DeltaId,
    ulong IntentSequence,
    ArchiveResult Archive,
    IReadOnlyList<ArchivedBlob> MetadataBlobs);

/// <summary>
/// The canonical publication order (specification 08 §10; FR-SNP-001,
/// FR-SNP-004, FR-SNP-005), as the only publication path: intent → scan →
/// segment/seal → upload → verify → index deltas → signed snapshot →
/// retirement → complete. The invariant underneath: <b>every object a
/// published object references is already durable</b> — steps 4 → 6 → 7 are
/// the ordering that guarantees it.
/// </summary>
/// <remarks>
/// Step 8's audit record is published for destructive purposes only —
/// specification 08 §6 defines audit records for destructive operations,
/// and a backup is not one; its step-8 action is the retirement event.
/// </remarks>
public sealed class PublicationOrchestrator
{
    private readonly CapturePolicy _policy;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyGeneration _generation;
    private readonly RepositoryKeySet _keys;
    private readonly KeyHierarchy _hierarchy;
    private readonly IObjectStore _store;
    private readonly WriterSequence _sequence;
    private readonly string _spoolDirectory;
    private readonly IPublicationObserver? _observer;

    /// <summary>Creates an orchestrator for one writer.</summary>
    public PublicationOrchestrator(
        CapturePolicy policy,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration generation,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        IObjectStore store,
        WriterSequence sequence,
        string spoolDirectory,
        IPublicationObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);

        _policy = policy;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _generation = generation;
        _keys = keys;
        _hierarchy = hierarchy;
        _store = store;
        _sequence = sequence;
        _spoolDirectory = spoolDirectory;
        _observer = observer;
    }

    /// <summary>Runs one publication end to end.</summary>
    public async ValueTask<PublishedSnapshot> PublishAsync(BackupJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var journal = new JournalPublisher(_store, _repositoryId, _writerId, _hierarchy, _sequence);
        using var indexPublisher = new IndexPublisher(_store, _repositoryId, _writerId, _hierarchy, _sequence);

        // Crash leftovers first: numbers a previous run allocated and never
        // accounted for get their void deltas (07 §4) before new work.
        foreach (var obligation in _sequence.RecoveredObligations)
        {
            await indexPublisher.PublishVoidDeltaAsync(_generation.Value, obligation, cancellationToken).ConfigureAwait(false);
        }

        // Step 1: the write intent, durable before any blob byte moves.
        var intentSequence = await journal.PublishAsync(
            JournalRecordKind.WriteIntent,
            new JournalPayload.WriteIntent(
                job.BackupSetId, [], job.DeclaredMaxDurationMs, job.ExpiryGeneration, IntentPurpose.Backup),
            job.NowUnixMilliseconds,
            _generation.Value,
            cancellationToken).ConfigureAwait(false);

        var scope = new ExtensionIntentScope(journal, intentSequence, job, _generation.Value);
        _observer?.AfterStep(PublicationStep.PublishIntent);

        // Step 2: scan. Phase 0's job is one stream; the scan is its metadata.
        _observer?.AfterStep(PublicationStep.ScanSource);

        // Steps 3–4: segment, compare, compress, encrypt, assemble, seal,
        // upload — each blob's covering extension durable before its put.
        var archiver = new FileArchiver(
            _policy, _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory, scope);
        var archive = await archiver.ArchiveAsync(job.Source, cancellationToken).ConfigureAwait(false);
        _observer?.AfterStep(PublicationStep.SegmentAndSeal);

        // The manifest graph rides in metadata blobs uploaded in the same
        // step-4 window; the snapshot's discoverable copy waits for step 7.
        var builder = new ManifestBuilder(
            _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory,
            _policy.BlobWriteProfile, scope);

        ObjectId fileVersionId, rootTreeId, policyId, snapshotObjectId;
        SnapshotManifest snapshot;
        byte[] encodedSnapshot;

        await using (builder.ConfigureAwait(false))
        {
            var fileVersion = ManifestBuilder.FromArchiveResult(
                archive, job.FileName, NameNormalisation.Unknown, EntryMetadata.Empty, parentVersion: null,
                job.CaptureDiagnostics);
            fileVersionId = await builder.AppendManifestAsync(
                ObjectType.FileVersionManifest, FileVersionManifestCodec.Encode(fileVersion), cancellationToken)
                .ConfigureAwait(false);

            var tree = new TreeManifest
            {
                Entries = [new TreeEntry(job.FileName, fileVersionId, EntryKind.File)],
                Name = "/"u8.ToArray(),
                NameNormalisation = NameNormalisation.Unknown,
                Metadata = EntryMetadata.Empty,
            };
            rootTreeId = await builder.AppendManifestAsync(
                ObjectType.TreeManifest, TreeManifestCodec.Encode(tree), cancellationToken).ConfigureAwait(false);

            var policy = new PolicyManifest
            {
                SegmentationProfile = _policy.SegmentationProfile.Value,
                SegmentSizeOrTarget = _policy.SegmentationProfile == Domain.Profiles.SegmentationProfile.CdcV1
                    ? (ulong)_policy.CdcParameters!.Value.TargetSize
                    : (ulong)_policy.SegmentSize.Bytes,
                CdcMinSize = _policy.CdcParameters is { } cdc ? (ulong)cdc.MinSize : null,
                CdcMaxSize = _policy.CdcParameters is { } cdcMax ? (ulong)cdcMax.MaxSize : null,
                CdcWindowSize = _policy.CdcParameters is not null ? (byte)CdcParameters.WindowSize : null,
                CompressionProfile = _policy.Compression.Profile.Value,
                CompressionThresholdPermille = _policy.Compression.ThresholdPermille,
                EncryptionProfile = _policy.EncryptionProfile.Value,
                BlobTargetSize = (ulong)_policy.BlobWriteProfile.TargetSizeBytes,
                BlobMaxSize = (ulong)_policy.BlobWriteProfile.MaximumSizeBytes,
                BlobMaxRecordCount = (uint)_policy.BlobWriteProfile.MaximumRecordCount,
                DedupTrustDomain = (byte)_policy.DedupTrustDomain,
            };
            policyId = await builder.AppendManifestAsync(
                ObjectType.PolicyManifest, PolicyManifestCodec.Encode(policy), cancellationToken).ConfigureAwait(false);

            snapshot = new SnapshotManifest
            {
                SnapshotId = job.SnapshotId,
                DeviceId = job.DeviceId,
                BackupSetId = job.BackupSetId,
                CaptureStartedAt = job.NowUnixMilliseconds,
                CaptureCompletedAt = job.NowUnixMilliseconds,
                RootTree = rootTreeId,
                PolicyManifest = policyId,
                ConsistencyMethod = 1,
                CaptureStatus = 1,
                SourceFilesystem = new SourceFilesystem(CaseSensitive: true, SupportsSparse: false, Name: "stream"),
                PublicationGeneration = _generation.Value,
                ClientVersion = job.ClientVersion,
            };

            using (var signer = RepositorySigner.Create(_hierarchy, _generation))
            {
                encodedSnapshot = SnapshotManifestCodec.Encode(
                    snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
            }

            snapshotObjectId = await builder.AppendManifestAsync(
                ObjectType.SnapshotManifest, encodedSnapshot, cancellationToken).ConfigureAwait(false);

            await builder.FlushAsync(cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.UploadBlobs);

            // Step 5: every put's acknowledgement was awaited and checked as
            // it happened; this step is where a batching store would drain.
            _observer?.AfterStep(PublicationStep.VerifyAcknowledgements);

            // Step 6: index deltas referencing the now-durable blobs —
            // entries projected from the sealed record tables (07 §10).
            var entries = new List<IndexEntry>();
            var covered = new List<BlobId>();
            foreach (var blob in archive.Blobs.Concat(builder.Blobs))
            {
                covered.Add(blob.BlobId);
                foreach (var record in blob.RecordTable)
                {
                    entries.Add(new IndexEntry(
                        record.ObjectId,
                        blob.BlobId,
                        record.PhysicalOffset,
                        record.StoredLength,
                        record.CompressionProfileValue,
                        record.EncryptionProfileValue,
                        IndexEntryType.Insertion));
                }
            }

            var deltaId = await indexPublisher.PublishDeltaAsync(
                _generation.Value, covered, entries, cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.PublishIndexDeltas);

            // Step 7: the snapshot's discoverable standalone copy — same
            // bytes, same object identifier as the in-blob record.
            await builder.WriteStandaloneSnapshotAsync(
                snapshot, encodedSnapshot, _sequence.AllocateNext(), cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.PublishSnapshot);

            // Step 8: retirement — an event, not a heartbeat (08 §5).
            await journal.PublishAsync(
                JournalRecordKind.IntentRetirement,
                new JournalPayload.IntentRetirement(intentSequence, IntentOutcome.Completed),
                job.NowUnixMilliseconds,
                _generation.Value,
                cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.RetireIntent);

            // Step 9: the local job is complete.
            _observer?.AfterStep(PublicationStep.Complete);

            return new PublishedSnapshot(
                snapshotObjectId, fileVersionId, rootTreeId, policyId, deltaId, intentSequence, archive, builder.Blobs);
        }
    }

    /// <summary>
    /// Covers blobs by intent-extension (08 §4): the extension naming a blob
    /// is durable before that blob is uploaded, each extension independently.
    /// </summary>
    private sealed class ExtensionIntentScope(
        JournalPublisher journal,
        ulong intentSequence,
        BackupJob job,
        uint generation) : IIntentScope
    {
        private readonly HashSet<BlobId> _covered = [];

        public async ValueTask EnsureCoveredAsync(BlobId blobId, CancellationToken cancellationToken)
        {
            if (!_covered.Add(blobId))
            {
                return;
            }

            await journal.PublishAsync(
                JournalRecordKind.IntentExtension,
                new JournalPayload.IntentExtension(intentSequence, [blobId], job.DeclaredMaxDurationMs),
                job.NowUnixMilliseconds,
                generation,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
