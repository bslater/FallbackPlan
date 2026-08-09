using Bodu;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>
/// One tree publication job: a scanner source and everything the snapshot
/// manifest needs. The rule <em>strings</em> ride here — the orchestrator
/// compiles them against the probed filesystem's case sensitivity and
/// refuses invalid rules before any byte is written (specification 06 §7.1:
/// writers MUST refuse), and the same strings land verbatim in the policy
/// manifest.
/// </summary>
public sealed record SnapshotJob
{
    /// <summary>The filesystem to capture from.</summary>
    public required IFileSystemSource Source { get; init; }

    /// <summary>The capture root.</summary>
    public required string RootPath { get; init; }

    /// <summary>Scanner switches; <see cref="ScanOptions.Rules"/> is ignored — rules come from the strings below.</summary>
    public ScanOptions ScanOptions { get; init; } = new();

    /// <summary>rules-v1 include rules (specification 06 §7.1).</summary>
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>rules-v1 exclude rules.</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    /// <summary>The claiming device (snapshot key 2), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> DeviceId { get; init; }

    /// <summary>The backup set (snapshot key 3), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> BackupSetId { get; init; }

    /// <summary>The snapshot identity (snapshot key 1), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>Parent snapshot identities, 16 bytes each.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ParentSnapshots { get; init; } = [];

    /// <summary>
    /// The catalogue-known snapshot this job is incremental against; null
    /// for a full backup. An unchanged file — same identity, size, and
    /// modification time as this snapshot's version of the same path
    /// (NFR-PERF-003) — re-emits the prior file-version reference without
    /// its content ever being read; one that only moved gets a new manifest
    /// rewritten from the prior one, still without reading its content.
    /// </summary>
    public ReadOnlyMemory<byte>? PriorSnapshotId { get; init; }

    /// <summary>Wall-clock now, epoch milliseconds.</summary>
    public required ulong NowUnixMilliseconds { get; init; }

    /// <summary>The declared maximum job duration for intent covering.</summary>
    public required ulong DeclaredMaxDurationMs { get; init; }

    /// <summary>The intent's expiry generation.</summary>
    public required ulong ExpiryGeneration { get; init; }

    /// <summary>The writing client's version string.</summary>
    public required string ClientVersion { get; init; }
}

/// <summary>
/// One published file version within a tree snapshot. <see cref="Reused"/>
/// marks the NFR-PERF-003 short-circuit: the object identifier names the
/// prior snapshot's version and no content was read.
/// </summary>
/// <remarks>
/// <c>Inherited</c> carries the manifest a renamed file's version was
/// rewritten from, when its content was inherited rather than captured.
/// <c>Archive</c> is null in that case — nothing was archived — so the
/// inherited manifest is what states the version's real length and hash to
/// the catalogue.
/// </remarks>
public sealed record PublishedFileVersion(
    string RelativePath,
    ReadOnlyMemory<byte> NameBytes,
    ObjectId ObjectId,
    EntryKind EntryKind,
    ArchiveResult? Archive,
    ulong? ModifiedAt = null,
    ulong? IdentityDevice = null,
    ulong? IdentityFileId = null,
    bool Reused = false,
    FileVersionManifest? Inherited = null);

/// <summary>The published outcome of a tree snapshot.</summary>
public sealed record PublishedTreeSnapshot(
    ObjectId SnapshotObjectId,
    ObjectId RootTreeObjectId,
    ObjectId PolicyObjectId,
    ObjectId? ErrorManifestObjectId,
    DeltaId DeltaId,
    ulong IntentSequence,
    IReadOnlyList<PublishedFileVersion> Files,
    IReadOnlyList<CaptureFailure> Failures,
    IReadOnlyList<ArchivedBlob> ContentBlobs,
    IReadOnlyList<ArchivedBlob> MetadataBlobs);

/// <summary>
/// The multi-file publication path: the same canonical nine-step order as
/// the single-stream path, generalised over a scanner event stream. Trees
/// are written bottom-up — every child manifest is appended before the
/// manifest that references it (invariant I1), sharding at the 06 §9
/// continuation boundary — and the capture semantics follow ADR-0026:
/// special files as zero-content versions, alternate streams as
/// single-segment records, hardlink groups keyed per §Decision 1,
/// <c>capture_status</c> 2 exactly when a non-empty error manifest exists.
/// </summary>
public sealed partial class PublicationOrchestrator
{
    /// <summary>Runs one tree publication end to end.</summary>
    public async ValueTask<PublishedTreeSnapshot> PublishAsync(SnapshotJob job, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(job);

        using var activity = EngineDiagnostics.Activities.StartActivity("publish");
        var publicationStarted = Stopwatch.GetTimestamp();

        // Probe first: rule case-sensitivity is the filesystem's, and the
        // snapshot records what was actually observed.
        var filesystem = job.Source.Probe(job.RootPath);

        if (!PathRuleSet.TryCreate(
                job.IncludeRules, job.ExcludeRules, caseSensitive: filesystem.CaseSensitive,
                out var rules, out var ruleDefects))
        {
            throw new ArgumentException(
                "The capture rules are invalid and a writer MUST refuse them (specification 06 §7.1): " +
                string.Join("; ", ruleDefects),
                nameof(job));
        }

        var options = job.ScanOptions with { Rules = rules };

        using var journal = new JournalPublisher(_store, _repositoryId, _writerId, _hierarchy, _sequence);
        using var indexPublisher = new IndexPublisher(_store, _repositoryId, _writerId, _hierarchy, _sequence);

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

        using var scope = new ExtensionIntentScope(
            journal, intentSequence, job.DeclaredMaxDurationMs, job.NowUnixMilliseconds, _generation.Value);
        _observer?.AfterStep(PublicationStep.PublishIntent);

        var archiver = new FileArchiver(
            _policy, _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory, scope);

        // One targeted reader serves both things this publication reads back:
        // a renamed file's prior manifest (architecture 06 §4.2) and the
        // verify-on-reuse confirmation (ADR-0006). Both want the same blob
        // cache, and neither exists without a catalogue to resolve a location.
        // Every catalogue call made while the pipeline is running goes
        // through one gate, because the pipeline is concurrent and a SQLite
        // connection is not (CatalogueGate).
        var gated = _catalogue is { } concurrentCatalogue ? new CatalogueGate(concurrentCatalogue) : null;

        using var reader = gated is not null
            ? new TargetedRecordReader(_store, _repositoryId, _keys, gated)
            : null;

        // The reuse decision, trust domain included (09 §5; FR-DED-002).
        // Without a catalogue there is no index to reuse from at all, so the
        // question never arises and the gate is absent rather than permissive.
        var trust = gated is not null
            ? new DedupTrustGate(_policy.DedupTrustDomain, _writerId, gated, reader!)
            : null;
        var dedup = trust is null ? null : (ReusePredicate)trust.MayReuseAsync;

        var builder = new ManifestBuilder(
            _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory,
            _policy.BlobWriteProfile, scope, dedup);

        var session = archiver.OpenSession(dedup);
        await using (builder.ConfigureAwait(false))
        await using (session.ConfigureAwait(false))
        {
            using var grouper = new HardlinkGrouper(_keys.ContentIdKey);
            using var sourceKeys = new SourceIdentityKeyDeriver(_keys.ContentIdKey);

            // The durable half of rename detection (06 §11), and the gate on
            // ever touching it: a catalogue that still holds identities has
            // already answered, so the hints are consulted only in the window
            // a rebuild opens — where identity is gone and paths are not.
            var hintBound = job.PriorSnapshotId is { } priorForIdentity && _catalogue is { } identityCatalogue
                && !identityCatalogue.HasIdentities(priorForIdentity.Span)
                ? identityCatalogue.LookupSnapshotCaptureTime(priorForIdentity.Span)
                : null;

            var walker = new TreeWalkPublisher(
                job, options, session, builder, grouper, gated, sourceKeys,
                hintBound is { } bound ? new HintSource(_store, _repositoryId, _keys, bound) : null,
                reader);

            // Steps 2–4 interleave by design: the scan streams, and each
            // file's content is archived as its event arrives — memory is
            // bounded by tree depth, never by file count.
            //
            // The reported state follows that interleaving rather than
            // pretending it does not happen: a job announces the latest stage
            // it entered, and the counts carry what is actually going on.
            // A pipeline that announces `Scanning` and then says nothing for
            // ten hours is the failure the 10 section 3 state machine exists
            // to prevent.
            var reporter = new PublicationProgress(_progress, job.SnapshotId);
            reporter.Enter(JobState.Scanning);

            await foreach (var scanEvent in job.Source
                .ScanAsync(job.RootPath, options, cancellationToken).ConfigureAwait(false))
            {
                await walker.ConsumeAsync(scanEvent, cancellationToken).ConfigureAwait(false);
                reporter.Observe(JobState.Packing, walker.Files, walker.Failures.Count);
            }

            var rootTreeId = walker.RootTreeId
                ?? throw new InvalidOperationException(Strings.PublicationOrchestrator_ScanProducedNoRootDirectory);
            _observer?.AfterStep(PublicationStep.ScanSource);

            reporter.Observe(JobState.Uploading, walker.Files, walker.Failures.Count);
            await session.FlushAsync(cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.SegmentAndSeal);
            reporter.Observe(JobState.Publishing, walker.Files, walker.Failures.Count);

            // The error manifest exists exactly when something failed —
            // and capture_status follows it (ADR-0026 §Decision 3).
            ObjectId? errorId = null;
            if (walker.Failures.Count > 0)
            {
                errorId = await builder.AppendManifestAsync(
                    ObjectType.ErrorManifest,
                    ErrorManifestCodec.Encode(new ErrorManifest(walker.Failures)),
                    cancellationToken).ConfigureAwait(false);
            }

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
                IncludeRules = job.IncludeRules,
                ExcludeRules = job.ExcludeRules,
            };
            var policyId = await builder.AppendManifestAsync(
                ObjectType.PolicyManifest, PolicyManifestCodec.Encode(policy), cancellationToken).ConfigureAwait(false);

            var snapshot = new SnapshotManifest
            {
                SnapshotId = job.SnapshotId,
                DeviceId = job.DeviceId,
                BackupSetId = job.BackupSetId,
                CaptureStartedAt = job.NowUnixMilliseconds,
                CaptureCompletedAt = job.NowUnixMilliseconds,
                RootTree = rootTreeId,
                ParentSnapshots = job.ParentSnapshots,
                PolicyManifest = policyId,
                ErrorManifest = errorId,
                ConsistencyMethod = 1,
                CaptureStatus = (byte)(errorId is null ? 1 : 2),
                SourceFilesystem = new SourceFilesystem(
                    filesystem.CaseSensitive,
                    filesystem.SupportsSparse,
                    filesystem.Name,
                    filesystem.MaxPathBytes,
                    filesystem.MaxComponentBytes,
                    filesystem.ReservedNames),
                PublicationGeneration = _generation.Value,
                ClientVersion = job.ClientVersion,
            };

            byte[] encodedSnapshot;
            using (var signer = RepositorySigner.Create(_hierarchy, _generation))
            {
                encodedSnapshot = SnapshotManifestCodec.Encode(
                    snapshot, signer.Sign(SnapshotManifestCodec.EncodeForSigning(snapshot)));
            }

            var snapshotObjectId = await builder.AppendManifestAsync(
                ObjectType.SnapshotManifest, encodedSnapshot, cancellationToken).ConfigureAwait(false);

            await builder.FlushAsync(cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.UploadBlobs);

            // Step 5: every put's acknowledgement was awaited as it happened.
            _observer?.AfterStep(PublicationStep.VerifyAcknowledgements);

            // Step 6: index deltas referencing the now-durable blobs.
            var entries = new List<IndexEntry>();
            var covered = new List<BlobId>();

            // The blob digest's durable home (07 §2.2). It is published
            // beside the blob it names, inside the signature, so a
            // participant receiving that blob can check the bytes against
            // something the writer signed rather than against a record kept
            // on the writer's own machine.
            var digests = new List<ReadOnlyMemory<byte>>();
            foreach (var blob in session.Blobs.Concat(builder.Blobs))
            {
                covered.Add(blob.BlobId);
                digests.Add(blob.Digest.ToArray());
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

            var (deltaId, delta) = await indexPublisher.PublishDeltaDetailedAsync(
                _generation.Value, covered, entries, digests, cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.PublishIndexDeltas);

            // Step 7: the snapshot's discoverable standalone copy — preceded
            // by the advisory source-identity hints, so a hint the next
            // publication wants is never published after the snapshot that
            // makes it findable (06 §11).
            await builder.WriteSourceIdentityHintsAsync(
                walker.SourceIdentities, intentSequence, cancellationToken).ConfigureAwait(false);

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

            // Step 9: the local job is complete — and the live catalogue
            // learns what was published without re-reading the store
            // (architecture 02 §7). A cache write, never a correctness step.
            if (_catalogue is not null)
            {
                ProjectIntoCatalogue(job, walker, session, builder, snapshotObjectId, rootTreeId, deltaId, delta, errorId);
            }

            EngineDiagnostics.PublicationDuration.Record(
                Stopwatch.GetElapsedTime(publicationStarted).TotalSeconds);
            _observer?.AfterStep(PublicationStep.Complete);

            return new PublishedTreeSnapshot(
                snapshotObjectId,
                rootTreeId,
                policyId,
                errorId,
                deltaId,
                intentSequence,
                walker.Files,
                walker.Failures,
                session.Blobs,
                builder.Blobs);
        }
    }

    /// <summary>
    /// Projects one completed publication into the live catalogue: blobs,
    /// the applied delta, the snapshot row with its capture time, every
    /// tree path, and each new file version with the identity and time the
    /// next incremental compares (NFR-PERF-003). Reused versions get a
    /// tree-entry row only — their file-version rows already exist.
    /// </summary>
    private void ProjectIntoCatalogue(
        SnapshotJob job,
        TreeWalkPublisher walker,
        ArchiveSession session,
        ManifestBuilder builder,
        ObjectId snapshotObjectId,
        ObjectId rootTreeId,
        DeltaId deltaId,
        IndexDelta delta,
        ObjectId? errorId)
    {
        foreach (var blob in session.Blobs)
        {
            _catalogue!.RecordBlob(
                blob.BlobId, blob.StoreBlobKey, BlobClass.Data, _generation,
                blob.RecordCount, blob.Length, [.. blob.Digest]);
        }

        foreach (var blob in builder.Blobs)
        {
            _catalogue!.RecordBlob(
                blob.BlobId, blob.StoreBlobKey, BlobClass.Metadata, _generation,
                blob.RecordCount, blob.Length, [.. blob.Digest]);
        }

        _catalogue!.ApplyDelta(deltaId, delta);

        // Signature state 1: this writer signed it in this process.
        _catalogue.RecordSnapshot(
            job.SnapshotId.Span, job.DeviceId.Span, job.BackupSetId.Span,
            snapshotObjectId, rootTreeId, _generation.Value,
            (byte)(errorId is null ? 1 : 2), signatureState: 1, capturedAt: job.NowUnixMilliseconds);

        foreach (var (path, treeId) in walker.Directories)
        {
            _catalogue.RecordTreeEntry(job.SnapshotId.Span, path, EntryKind.DirectoryPlaceholder, treeId);
        }

        foreach (var file in walker.Files)
        {
            _catalogue.RecordTreeEntry(job.SnapshotId.Span, file.RelativePath, file.EntryKind, file.ObjectId);

            if (file.Reused)
            {
                continue;
            }

            // A renamed file archived nothing, so its content facts come from
            // the manifest it inherited. Recording zeroes there would leave
            // the next incremental unable to short-circuit a file that has not
            // changed since before it moved.
            var archive = file.Archive;
            var inherited = file.Inherited;
            _catalogue.RecordFileVersion(
                file.ObjectId,
                file.NameBytes.Span,
                file.EntryKind,
                (ulong)(archive?.LogicalLength ?? (long?)inherited?.LogicalLength ?? 0),
                archive is not null ? [.. archive.WholeFileHash]
                    : inherited is not null ? inherited.WholeFileHash.ToArray()
                    : TreeWalkPublisher.EmptyHash,
                parentVersion: null,
                archive?.SegmentReferences.Count ?? inherited?.SegmentReferences.Count ?? 0,
                file.ModifiedAt,
                file.IdentityDevice,
                file.IdentityFileId);

            if (archive is not null)
            {
                for (var i = 0; i < archive.SegmentReferences.Count; i++)
                {
                    _catalogue.RecordSegmentDedup(archive.SegmentContentIds[i], archive.SegmentReferences[i].ObjectId);
                }
            }
        }

        _catalogue.SetSource("live");
    }

    /// <summary>
    /// Consumes the scan event stream and turns it into the manifest graph:
    /// a frame per open directory, entries accumulating in the scanner's
    /// byte-sorted order, each directory's tree chain written at its
    /// <see cref="ScanEvent.LeaveDirectory"/> — children durable-appended
    /// before the parent that names them.
    /// </summary>
    private sealed class TreeWalkPublisher(
        SnapshotJob job,
        ScanOptions options,
        ArchiveSession session,
        ManifestBuilder builder,
        HardlinkGrouper grouper,
        CatalogueGate? catalogue,
        SourceIdentityKeyDeriver sourceKeys,
        HintSource? hints,
        TargetedRecordReader? manifests)
    {
        internal static readonly byte[] EmptyHash = SHA256.HashData([]);

        private readonly Stack<Frame> _frames = new();
        private readonly List<PublishedFileVersion> _files = [];
        private readonly List<CaptureFailure> _failures = [];
        private readonly List<(string Path, ObjectId ObjectId)> _directories = [];
        private readonly Dictionary<SourceKey, SourceIdentityHint?> _hints = [];

        private sealed record Frame(ScanEntry Directory, List<TreeEntry> Entries);

        public ObjectId? RootTreeId { get; private set; }

        public IReadOnlyList<PublishedFileVersion> Files => _files;

        public List<CaptureFailure> Failures => _failures;

        /// <summary>Every published subdirectory (path, head tree id); the root is the snapshot's own row.</summary>
        public IReadOnlyList<(string Path, ObjectId ObjectId)> Directories => _directories;

        /// <summary>
        /// The 06 §11 hints this snapshot owes: one per file version it
        /// <em>created</em>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A version that was reused was created by an earlier snapshot and
        /// already has its hint, which still names it — re-publishing that is
        /// the whole per-snapshot cost this layout exists to remove. A rename
        /// does produce a new manifest, because a manifest states its own
        /// name, so a rename does get a hint.
        /// </para>
        /// <para>
        /// A source key claimed by two versions in one snapshot is dropped
        /// rather than resolved: that is a hardlink group's two names, and
        /// asserting either as the other's ancestor would be a coin toss a
        /// manifest keeps forever.
        /// </para>
        /// </remarks>
        public IReadOnlyList<SourceIdentityHint> SourceIdentities =>
            [.. _hints.Values.Where(static hint => hint is not null).Select(static hint => hint!)];

        public async ValueTask ConsumeAsync(ScanEvent scanEvent, CancellationToken cancellationToken)
        {
            switch (scanEvent)
            {
                case ScanEvent.EnterDirectory enter:
                    _frames.Push(new Frame(enter.Entry, []));
                    break;

                case ScanEvent.LeaveDirectory:
                    await CloseDirectoryAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case ScanEvent.Leaf leaf:
                    await PublishLeafAsync(leaf.Entry, cancellationToken).ConfigureAwait(false);
                    break;

                case ScanEvent.Failure failure:
                    _failures.Add(ToCaptureFailure(failure.Detail));
                    break;

                default:
                    throw new InvalidOperationException(Strings.FormatFrame_UnknownScanEvent(scanEvent.GetType().Name));
            }
        }

        private async ValueTask CloseDirectoryAsync(CancellationToken cancellationToken)
        {
            var frame = _frames.Pop();
            var isRoot = _frames.Count == 0;

            var name = isRoot ? "/"u8.ToArray() : frame.Directory.NameBytes;
            var headId = await TreeChainWriter.WriteAsync(
                builder, frame.Entries, name, frame.Directory.NameNormalisation, frame.Directory.Metadata,
                cancellationToken).ConfigureAwait(false);

            if (isRoot)
            {
                RootTreeId = headId;
            }
            else
            {
                _frames.Peek().Entries.Add(
                    new TreeEntry(frame.Directory.NameBytes, headId, EntryKind.DirectoryPlaceholder));
                _directories.Add((frame.Directory.RelativePath, headId));
            }
        }

        private async ValueTask PublishLeafAsync(ScanEntry entry, CancellationToken cancellationToken)
        {
            // The prior version of THIS file, found by path where the path is
            // unchanged and by stable identity where it moved. Identity is
            // what makes a rename the same file rather than a delete plus a
            // create (architecture 06 §1) — both for the content
            // short-circuits below and for the ancestry the manifest records.
            var prior = FindPriorVersion(entry);
            var unchanged = IsContentUnchanged(entry, prior);

            // The NFR-PERF-003 short-circuit: nothing about the file changed
            // and it is still where it was — re-emit the prior version's
            // reference; the content is never opened.
            if (unchanged && string.Equals(prior!.Path, entry.RelativePath, StringComparison.Ordinal))
            {
                EngineDiagnostics.ScanFiles.Add(1, new KeyValuePair<string, object?>("outcome", "reused"));
                _frames.Peek().Entries.Add(new TreeEntry(entry.NameBytes, prior.ObjectId, EntryKind.File));
                _files.Add(new PublishedFileVersion(
                    entry.RelativePath, entry.NameBytes, prior.ObjectId, EntryKind.File, Archive: null,
                    prior.ModifiedAt, prior.IdentityDevice, prior.IdentityFileId, Reused: true));
                return;
            }

            // The same file, moved. Its reference cannot be re-emitted — a
            // manifest states its own name (06 §4), so a tree entry under the
            // new name pointing at the old manifest would restore the file
            // under the name it no longer has. It needs a new manifest, but
            // not a new read: the bytes are unchanged and already durable, so
            // the prior manifest is fetched and rewritten with the new name.
            if (unchanged &&
                await ReadPriorManifestAsync(prior!.ObjectId, cancellationToken).ConfigureAwait(false) is { } moved)
            {
                await PublishRenameAsync(entry, prior, moved, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var manifest = entry.Kind switch
                {
                    ScanEntryKind.File => await CaptureFileAsync(entry, cancellationToken).ConfigureAwait(false),
                    ScanEntryKind.Symlink => ZeroContentVersion(entry, EntryKind.Symlink),
                    ScanEntryKind.Special => ZeroContentVersion(entry, EntryKind.Special),
                    _ => throw new InvalidOperationException(Strings.FormatFrame_EntryCannotLeaf(entry.Kind)),
                };

                if (manifest is null)
                {
                    return; // the failure was already recorded
                }

                // The ancestor, when there is one. A file version whose parent
                // is null claims to be the first version of that file; saying
                // so about the fourth edit of a document, or about a file the
                // user merely renamed, is the history loss FR-MAN-003 exists
                // to prevent.
                var ancestor = prior?.ObjectId
                    ?? await FindPriorVersionByHintAsync(entry, cancellationToken).ConfigureAwait(false);
                var withParent = ancestor is { } parentVersion
                    ? manifest with { ParentVersion = parentVersion }
                    : manifest;

                var objectId = await builder.AppendManifestAsync(
                    ObjectType.FileVersionManifest, FileVersionManifestCodec.Encode(withParent), cancellationToken)
                    .ConfigureAwait(false);

                EngineDiagnostics.ScanFiles.Add(1, new KeyValuePair<string, object?>("outcome", "captured"));
                EngineDiagnostics.ScanBytes.Add(entry.Length);
                _frames.Peek().Entries.Add(new TreeEntry(entry.NameBytes, objectId, manifest.EntryKind));
                _files.Add(new PublishedFileVersion(
                    entry.RelativePath, entry.NameBytes, objectId, manifest.EntryKind, LastArchive,
                    entry.Metadata.ModifiedAt, entry.Identity?.Device, entry.Identity?.FileId));
                RecordSourceIdentity(entry, objectId);
                LastArchive = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                EngineDiagnostics.ScanFiles.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                _failures.Add(ToCaptureFailure(entry.RelativePath, exception));
            }
        }

        /// <summary>
        /// Whether <paramref name="entry"/>'s content is provably the same as
        /// <paramref name="prior"/>'s — identity, size, and modification time
        /// all present and all equal.
        /// </summary>
        /// <remarks>
        /// All three must be present, not merely non-contradictory. A rebuilt
        /// catalogue holds no identities, so it disables both short-circuits
        /// rather than weakening either: without identity, size and time alone
        /// cannot tell an unchanged file from a different file at the same
        /// path.
        /// </remarks>
        private static bool IsContentUnchanged(ScanEntry entry, CatalogueTreeEntry? prior) =>
            entry.Kind == ScanEntryKind.File &&
            prior is { EntryKind: EntryKind.File } &&
            prior.ModifiedAt is { } priorModified && entry.Metadata.ModifiedAt == priorModified &&
            prior.LogicalLength is { } priorLength && (ulong)entry.Length == priorLength &&
            prior.IdentityDevice is { } priorDevice && prior.IdentityFileId is { } priorFileId &&
            entry.Identity is { } identity &&
            identity.Device == priorDevice && identity.FileId == priorFileId;

        /// <summary>
        /// The prior version's manifest, when this publication can read one
        /// (architecture 06 §4.2). Null disables the rename rewrite and the
        /// file is captured normally.
        /// </summary>
        private ValueTask<FileVersionManifest?> ReadPriorManifestAsync(
            ObjectId objectId, CancellationToken cancellationToken) =>
            manifests is null
                ? ValueTask.FromResult<FileVersionManifest?>(null)
                : manifests.TryReadFileVersionAsync(objectId, cancellationToken);

        /// <summary>
        /// Publishes a moved file by rewriting its prior manifest under the
        /// new name, without reading a byte of its content.
        /// </summary>
        /// <remarks>
        /// Exactly three fields change: the name, its normalisation, and the
        /// ancestry. Everything else — the segment references, the whole-file
        /// hash, the length, the sparse extents, the segmentation profile, the
        /// metadata map, the hardlink group, the capture diagnostics — is
        /// inherited verbatim, because it describes bytes that were not
        /// re-examined and must not be re-stated as though they had been. That
        /// is the same fidelity the unchanged-path short-circuit above already
        /// gives, which re-emits the prior manifest whole.
        /// </remarks>
        private async ValueTask PublishRenameAsync(
            ScanEntry entry,
            CatalogueTreeEntry prior,
            FileVersionManifest priorManifest,
            CancellationToken cancellationToken)
        {
            var renamed = priorManifest with
            {
                Name = entry.NameBytes,
                NameNormalisation = entry.NameNormalisation,
                ParentVersion = prior.ObjectId,
            };

            var objectId = await builder.AppendManifestAsync(
                ObjectType.FileVersionManifest, FileVersionManifestCodec.Encode(renamed), cancellationToken)
                .ConfigureAwait(false);

            EngineDiagnostics.ScanFiles.Add(1, new KeyValuePair<string, object?>("outcome", "renamed"));
            _frames.Peek().Entries.Add(new TreeEntry(entry.NameBytes, objectId, EntryKind.File));
            _files.Add(new PublishedFileVersion(
                entry.RelativePath, entry.NameBytes, objectId, EntryKind.File, Archive: null,
                entry.Metadata.ModifiedAt, entry.Identity?.Device, entry.Identity?.FileId, Inherited: renamed));
            RecordSourceIdentity(entry, objectId);
        }

        /// <summary>
        /// The prior snapshot's version of this entry — by path first, then by
        /// stable identity.
        /// </summary>
        /// <param name="entry">The entry being captured.</param>
        /// <returns>The prior version, or <see langword="null"/> when this is a new file.</returns>
        /// <remarks>
        /// Path first because it is the overwhelmingly common case and the
        /// cheaper index. Identity second because a rename or a move changes
        /// the path and changes nothing else, and treating that as a new file
        /// costs a full re-read of unchanged bytes and severs the version
        /// history the user was trying to keep.
        /// </remarks>
        private CatalogueTreeEntry? FindPriorVersion(ScanEntry entry)
        {
            if (catalogue is null || job.PriorSnapshotId is not { } priorSnapshot)
            {
                return null;
            }

            if (catalogue.Read(c => c.LookupPath(priorSnapshot.Span, entry.RelativePath)) is { } byPath)
            {
                return byPath;
            }

            return entry.Identity is { } identity
                ? catalogue.Read(c => c.LookupIdentity(priorSnapshot.Span, identity.Device, identity.FileId))
                : null;
        }

        /// <summary>
        /// The prior version named by the source-identity hints (specification
        /// 06 §11), when the catalogue could not answer.
        /// </summary>
        /// <remarks>
        /// This is the case a rebuilt catalogue produces: paths survive a
        /// forensic rebuild, identities do not, so a file that was renamed
        /// since the last snapshot matches neither index. Without the hints
        /// its new version would claim to be the file's first — a durable loss
        /// caused by a transient cache state. It yields <b>ancestry only</b>;
        /// the content short-circuit above still needs size and modification
        /// time, which a hint does not carry.
        /// </remarks>
        private async ValueTask<ObjectId?> FindPriorVersionByHintAsync(
            ScanEntry entry, CancellationToken cancellationToken)
        {
            if (hints is null || entry.Identity is not { } identity)
            {
                return null;
            }

            return await hints
                .FindAsync(sourceKeys.Derive(job.DeviceId.Span, identity.FileId), cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records this snapshot's own hint for <paramref name="objectId"/>,
        /// so the next publication can find it after a catalogue rebuild.
        /// </summary>
        private void RecordSourceIdentity(ScanEntry entry, ObjectId objectId)
        {
            if (entry.Identity is not { } identity)
            {
                return;
            }

            var sourceKey = sourceKeys.Derive(job.DeviceId.Span, identity.FileId);
            var key = SourceKey.From(sourceKey);

            if (_hints.TryGetValue(key, out var existing))
            {
                // Two versions, one inode, one snapshot: a hardlink group's
                // two names. Neither is the other's ancestor and nothing here
                // can tell which the next rename will mean, so the source key
                // answers nothing at all.
                if (existing is null || existing.ObjectId != objectId)
                {
                    _hints[key] = null;
                }

                return;
            }

            _hints[key] = new SourceIdentityHint
            {
                SourceKey = sourceKey,
                SnapshotId = job.SnapshotId,
                ObjectId = objectId,
                CapturedAt = job.NowUnixMilliseconds,
            };
        }

        private ArchiveResult? LastArchive { get; set; }

        private async ValueTask<FileVersionManifest?> CaptureFileAsync(ScanEntry entry, CancellationToken cancellationToken)
        {
            var diagnostics = new List<string>(entry.Diagnostics);

            ArchiveResult? archive = null;
            var attempts = 0;
            var consistent = false;
            var substituted = false;

            // Read, then revalidate: a file that changed mid-read is read
            // again, up to the option's bound; content is always a complete
            // read, never a torn one (architecture 06 §1; ADR-0026 §Decision 2).
            while (attempts < Math.Max(1, options.ReadAttempts))
            {
                attempts++;

                var stream = job.Source.OpenRead(entry);
                await using (stream.ConfigureAwait(false))
                {
                    archive = entry.SparseExtents.Count > 0 && stream.CanSeek
                        ? await session.ArchiveSparseFileAsync(
                            stream, entry.SparseExtents, stream.Length, cancellationToken).ConfigureAwait(false)
                        : await session.ArchiveFileAsync(stream, priorVersion: null, cancellationToken).ConfigureAwait(false);
                }

                var probe = job.Source.Revalidate(entry);

                // Identity first. Size and modification time detect an
                // ordinary edit; they do not detect the object at this name
                // being replaced by another one, which is what a
                // time-of-check-to-time-of-use attack does — and re-reading
                // would only read the substitute again, so it is recorded and
                // the loop stops rather than retrying.
                if (probe?.Identity is { } observed && entry.Identity is { } expected &&
                    (observed.Device != expected.Device || observed.FileId != expected.FileId))
                {
                    substituted = true;
                    break;
                }

                if (probe is null ||
                    (probe.Length == archive.LogicalLength &&
                     (probe.ModifiedAtMs is null || entry.Metadata.ModifiedAt is null ||
                      probe.ModifiedAtMs == entry.Metadata.ModifiedAt)))
                {
                    consistent = true;
                    break;
                }
            }

            if (substituted)
            {
                diagnostics.Add("captured-identity-changed");
            }
            else if (!consistent)
            {
                diagnostics.Add($"captured-inconsistent: {attempts}");
            }

            LastArchive = archive;

            return new FileVersionManifest
            {
                EntryKind = EntryKind.File,
                Name = entry.NameBytes,
                NameNormalisation = entry.NameNormalisation,
                LogicalLength = (ulong)archive!.LogicalLength,
                SegmentReferences = archive.SegmentReferences,
                SparseExtents = entry.SparseExtents.Count > 0 && archive.SegmentReferences.Count > 0 &&
                    (ulong)archive.SegmentReferences.Sum(r => r.LogicalLength) < (ulong)archive.LogicalLength
                    ? entry.SparseExtents
                    : [],
                WholeFileHash = archive.WholeFileHash.ToArray(),
                SegmentationProfile = archive.SegmentationProfile.Value,
                Metadata = await CaptureAlternateStreamsAsync(entry, cancellationToken).ConfigureAwait(false),
                HardlinkGroup = entry.Identity is { LinkCount: > 1 } identity
                    ? (ReadOnlyMemory<byte>?)grouper.Derive(job.DeviceId.Span, identity.FileId)
                    : null,
                CaptureDiagnostics = diagnostics,
            };
        }

        private FileVersionManifest ZeroContentVersion(ScanEntry entry, EntryKind kind) => new()
        {
            EntryKind = kind,
            Name = entry.NameBytes,
            NameNormalisation = entry.NameNormalisation,
            LogicalLength = 0,
            SegmentReferences = [],
            WholeFileHash = EmptyHash,
            SegmentationProfile = 0,
            Metadata = entry.Metadata,
            LinkTarget = entry.LinkTarget,
            HardlinkGroup = entry.Identity is { LinkCount: > 1 } identity
                ? (ReadOnlyMemory<byte>?)grouper.Derive(job.DeviceId.Span, identity.FileId)
                : null,
            CaptureDiagnostics = entry.Diagnostics,
        };

        /// <summary>
        /// Archives each named alternate stream as a single-segment record
        /// (ADR-0026 §Decision 5); one that does not fit becomes error
        /// reason 6 naming the stream, and the file itself still captures.
        /// </summary>
        private async ValueTask<EntryMetadata> CaptureAlternateStreamsAsync(
            ScanEntry entry, CancellationToken cancellationToken)
        {
            if (entry.AlternateStreamNames.Count == 0)
            {
                return entry.Metadata;
            }

            var streams = new List<AlternateStreamEntry>();
            foreach (var (streamName, _) in entry.AlternateStreamNames)
            {
                try
                {
                    var stream = job.Source.OpenAlternateStream(entry, streamName);
                    await using (stream.ConfigureAwait(false))
                    {
                        var record = await session.TryArchiveSingleSegmentAsync(stream, cancellationToken)
                            .ConfigureAwait(false);

                        if (record is null)
                        {
                            _failures.Add(new CaptureFailure(
                                SplitPath(entry.RelativePath),
                                CaptureFailureReason.TooLarge,
                                $"Alternate stream '{streamName}' exceeds one segment — the v1 bound (ADR-0026 §Decision 5)."));
                            continue;
                        }

                        streams.Add(new AlternateStreamEntry(
                            Encoding.UTF8.GetBytes(streamName), record.ObjectId, record.Length));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _failures.Add(new CaptureFailure(
                        SplitPath(entry.RelativePath),
                        ClassifyFailure(exception),
                        $"Alternate stream '{streamName}': {exception.Message}"));
                }
            }

            return entry.Metadata with { AlternateStreams = streams };
        }

        private static CaptureFailure ToCaptureFailure(ScanFailure failure) =>
            new(SplitPath(failure.RelativePath), failure.Reason, failure.Detail);

        private static CaptureFailure ToCaptureFailure(string relativePath, Exception exception) =>
            new(SplitPath(relativePath), ClassifyFailure(exception), exception.Message);

        private static CaptureFailureReason ClassifyFailure(Exception exception) => exception switch
        {
            UnauthorizedAccessException => CaptureFailureReason.Permission,
            FileNotFoundException or DirectoryNotFoundException => CaptureFailureReason.NotFound,
            _ => CaptureFailureReason.IoError,
        };

        private static List<ReadOnlyMemory<byte>> SplitPath(string relativePath) =>
            [.. relativePath.Split('/').Select(component => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(component))];
    }

    /// <summary>
    /// Turns the publication's own knowledge into progress a client can watch.
    /// </summary>
    /// <remarks>
    /// Counts only — files and bytes, never a path or a filename. Progress
    /// travels to an authenticated caller and may carry job identity for that
    /// reason (ADR-0029 section 5), but nothing here needs to name a file, so
    /// nothing here does.
    /// </remarks>
    private sealed class PublicationProgress(IJobProgressReporter? reporter, ReadOnlyMemory<byte> snapshotId)
    {
        private readonly string _jobId = Convert.ToHexString(snapshotId.Span).ToLowerInvariant();

        public void Enter(JobState state) => Emit(state, [], 0);

        public void Observe(JobState state, IReadOnlyList<PublishedFileVersion> files, int failures) =>
            Emit(state, files, failures);

        private void Emit(JobState state, IReadOnlyList<PublishedFileVersion> files, int failures)
        {
            if (reporter is null)
            {
                return;
            }

            long reused = 0;
            long seen = 0;
            long stored = 0;
            foreach (var file in files)
            {
                if (file.Reused)
                {
                    reused++;
                }

                if (file.Archive is { } archive)
                {
                    seen += archive.LogicalLength;
                    foreach (var blob in archive.Blobs)
                    {
                        stored += blob.Length;
                    }
                }
            }

            reporter.Report(new JobProgress(_jobId, state, files.Count, files.Count, reused, failures, seen, stored));
        }
    }
}

/// <summary>
/// Writes one directory's tree chain (specification 06 §5, §9): entries are
/// split into shards below the metadata object limit, the <b>last</b> shard
/// is appended first so every continuation names an already-appended
/// manifest, and the head — carrying name, normalisation, and metadata —
/// goes last; its object identifier is what the parent's entry names.
/// </summary>
public static class TreeChainWriter
{
    /// <summary>
    /// The per-shard budget for estimated entry bytes. Held well under the
    /// 16 MiB metadata object limit because the estimate is per-entry
    /// (name + object id + array overhead) and deliberately conservative.
    /// </summary>
    public const int DefaultShardBudget = 15 * 1024 * 1024;

    /// <summary>Writes the chain and returns the head manifest's object identifier.</summary>
    public static async ValueTask<ObjectId> WriteAsync(
        ManifestBuilder builder,
        IReadOnlyList<TreeEntry> entries,
        ReadOnlyMemory<byte> name,
        NameNormalisation normalisation,
        EntryMetadata metadata,
        CancellationToken cancellationToken,
        int shardBudget = DefaultShardBudget)
    {
        var shards = Shard(entries, shardBudget);

        // Continuations point forward, so the chain is written backwards:
        // the tail first, each predecessor naming an appended successor.
        ObjectId? continuation = null;
        for (var i = shards.Count - 1; i >= 1; i--)
        {
            var shard = new TreeManifest { Entries = shards[i], Continuation = continuation };
            continuation = await builder.AppendManifestAsync(
                ObjectType.TreeManifest, TreeManifestCodec.Encode(shard), cancellationToken).ConfigureAwait(false);
        }

        var head = new TreeManifest
        {
            Entries = shards[0],
            Name = name,
            NameNormalisation = normalisation,
            Metadata = metadata,
            Continuation = continuation,
        };

        return await builder.AppendManifestAsync(
            ObjectType.TreeManifest, TreeManifestCodec.Encode(head), cancellationToken).ConfigureAwait(false);
    }

    private static List<List<TreeEntry>> Shard(IReadOnlyList<TreeEntry> entries, int shardBudget)
    {
        var shards = new List<List<TreeEntry>> { new() };
        var budget = 0;

        foreach (var entry in entries)
        {
            var estimate = entry.Name.Length + ObjectId.Size + 16;
            if (budget + estimate > shardBudget && shards[^1].Count > 0)
            {
                shards.Add([]);
                budget = 0;
            }

            shards[^1].Add(entry);
            budget += estimate;
        }

        return shards;
    }
}
