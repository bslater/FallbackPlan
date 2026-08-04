using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;

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

    /// <summary>Wall-clock now, epoch milliseconds.</summary>
    public required ulong NowUnixMilliseconds { get; init; }

    /// <summary>The declared maximum job duration for intent covering.</summary>
    public required ulong DeclaredMaxDurationMs { get; init; }

    /// <summary>The intent's expiry generation.</summary>
    public required ulong ExpiryGeneration { get; init; }

    /// <summary>The writing client's version string.</summary>
    public required string ClientVersion { get; init; }
}

/// <summary>One published file version within a tree snapshot.</summary>
public sealed record PublishedFileVersion(
    string RelativePath,
    ObjectId ObjectId,
    EntryKind EntryKind,
    ArchiveResult? Archive);

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
        ArgumentNullException.ThrowIfNull(job);

        // Probe first: rule case-sensitivity is the filesystem's, and the
        // snapshot records what was actually observed.
        var filesystem = job.Source.Probe(job.RootPath);

        if (!PathRuleSet.TryCreate(
                job.IncludeRules, job.ExcludeRules, caseSensitive: filesystem.CaseSensitive,
                out var rules, out var ruleDefect))
        {
            throw new ArgumentException(
                $"The capture rules are invalid and a writer MUST refuse them (specification 06 §7.1): {ruleDefect}",
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

        var scope = new ExtensionIntentScope(
            journal, intentSequence, job.DeclaredMaxDurationMs, job.NowUnixMilliseconds, _generation.Value);
        _observer?.AfterStep(PublicationStep.PublishIntent);

        var archiver = new FileArchiver(
            _policy, _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory, scope);
        var builder = new ManifestBuilder(
            _repositoryId, _writerId, _generation, _keys, _store, _sequence, _spoolDirectory,
            _policy.BlobWriteProfile, scope);

        var session = archiver.OpenSession();
        await using (builder.ConfigureAwait(false))
        await using (session.ConfigureAwait(false))
        {
            using var grouper = new HardlinkGrouper(_keys.ContentIdKey);

            var walker = new TreeWalkPublisher(job, options, session, builder, grouper);

            // Steps 2–4 interleave by design: the scan streams, and each
            // file's content is archived as its event arrives — memory is
            // bounded by tree depth, never by file count.
            await foreach (var scanEvent in job.Source
                .ScanAsync(job.RootPath, options, cancellationToken).ConfigureAwait(false))
            {
                await walker.ConsumeAsync(scanEvent, cancellationToken).ConfigureAwait(false);
            }

            var rootTreeId = walker.RootTreeId
                ?? throw new InvalidOperationException("The scan produced no root directory.");
            _observer?.AfterStep(PublicationStep.ScanSource);

            await session.FlushAsync(cancellationToken).ConfigureAwait(false);
            _observer?.AfterStep(PublicationStep.SegmentAndSeal);

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
            foreach (var blob in session.Blobs.Concat(builder.Blobs))
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

            // Step 7: the snapshot's discoverable standalone copy.
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
        HardlinkGrouper grouper)
    {
        private static readonly byte[] EmptyHash = SHA256.HashData([]);

        private readonly Stack<Frame> _frames = new();
        private readonly List<PublishedFileVersion> _files = [];
        private readonly List<CaptureFailure> _failures = [];

        private sealed record Frame(ScanEntry Directory, List<TreeEntry> Entries);

        public ObjectId? RootTreeId { get; private set; }

        public IReadOnlyList<PublishedFileVersion> Files => _files;

        public List<CaptureFailure> Failures => _failures;

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
                    throw new InvalidOperationException($"Unknown scan event {scanEvent.GetType().Name}.");
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
            }
        }

        private async ValueTask PublishLeafAsync(ScanEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                var manifest = entry.Kind switch
                {
                    ScanEntryKind.File => await CaptureFileAsync(entry, cancellationToken).ConfigureAwait(false),
                    ScanEntryKind.Symlink => ZeroContentVersion(entry, EntryKind.Symlink),
                    ScanEntryKind.Special => ZeroContentVersion(entry, EntryKind.Special),
                    _ => throw new InvalidOperationException($"A {entry.Kind} entry cannot be a leaf."),
                };

                if (manifest is null)
                {
                    return; // the failure was already recorded
                }

                var objectId = await builder.AppendManifestAsync(
                    ObjectType.FileVersionManifest, FileVersionManifestCodec.Encode(manifest), cancellationToken)
                    .ConfigureAwait(false);

                _frames.Peek().Entries.Add(new TreeEntry(entry.NameBytes, objectId, manifest.EntryKind));
                _files.Add(new PublishedFileVersion(entry.RelativePath, objectId, manifest.EntryKind, LastArchive));
                LastArchive = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failures.Add(ToCaptureFailure(entry.RelativePath, exception));
            }
        }

        private ArchiveResult? LastArchive { get; set; }

        private async ValueTask<FileVersionManifest?> CaptureFileAsync(ScanEntry entry, CancellationToken cancellationToken)
        {
            var diagnostics = new List<string>(entry.Diagnostics);

            ArchiveResult? archive = null;
            var attempts = 0;
            var consistent = false;

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
                if (probe is null ||
                    (probe.Length == archive.LogicalLength &&
                     (probe.ModifiedAtMs is null || entry.Metadata.ModifiedAt is null ||
                      probe.ModifiedAtMs == entry.Metadata.ModifiedAt)))
                {
                    consistent = true;
                    break;
                }
            }

            if (!consistent)
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
