using System.Buffers;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Segmentation;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// Archives one file version: segments the source, hashes and identifies each
/// segment in the same pass, compresses under the storage threshold,
/// encrypts into records, seals blobs at the configured targets, and uploads
/// each sealed blob under its store blob key — the specification 04 §5 order
/// throughout, with memory bounded by the segment size (FR-ARCH-001,
/// FR-ARCH-003, FR-ARCH-005, NFR-PERF-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Uploading is not publication</b> (specification 05 §7): nothing here
/// publishes a snapshot — that is <see cref="PublicationOrchestrator"/>'s
/// job. When an <see cref="IIntentScope"/> is supplied, every blob's upload
/// is preceded by a durable covering intent (specification 08 §3.1); without
/// one, this type serves non-publishing paths — tests, probes, tools.
/// </para>
/// <para>
/// An <c>AlreadyExists</c> outcome on upload is idempotent-retry success:
/// store objects are immutable and blob identifiers unique, so an existing
/// object with our key carries our bytes (specification 01 §4).
/// </para>
/// </remarks>
public sealed class FileArchiver
{
    private readonly CapturePolicy _policy;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyGeneration _generation;
    private readonly RepositoryKeySet _keys;
    private readonly IObjectStore _store;
    private readonly IBlobCounterAllocator _counters;
    private readonly string _spoolDirectory;
    private readonly SpoolPinnedConfiguration _pinned;
    private readonly IIntentScope? _intentScope;

    /// <summary>Creates an archiver over a validated policy.</summary>
    /// <exception cref="ArgumentException">The policy is invalid — the message names each defect (FR-ARCH-007).</exception>
    public FileArchiver(
        CapturePolicy policy,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration generation,
        RepositoryKeySet keys,
        IObjectStore store,
        IBlobCounterAllocator counters,
        string spoolDirectory,
        IIntentScope? intentScope = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);

        var validation = policy.ValidateAgainstStore(store.Capabilities.MaximumObjectSize);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "The capture policy is invalid: " + string.Join(", ", validation.Defects.Select(defect => defect.Name)),
                nameof(policy));
        }

        _policy = policy;
        _pinned = SpoolPinnedConfiguration.FromPolicy(
            policy,
            policy.Compression.Profile == CompressionProfile.ZstdV1 ? ZstdSegmentCodec.CodecVersion : "none");
        _repositoryId = repositoryId;
        _writerId = writerId;
        _generation = generation;
        _keys = keys;
        _store = store;
        _counters = counters;
        _spoolDirectory = spoolDirectory;
        _intentScope = intentScope;
    }

    /// <summary>Archives <paramref name="source"/> as a new file version.</summary>
    public ValueTask<ArchiveResult> ArchiveAsync(Stream source, CancellationToken cancellationToken) =>
        ArchiveAsync(source, priorVersion: null, cancellationToken);

    /// <summary>
    /// Archives <paramref name="source"/>, comparing against
    /// <paramref name="priorVersion"/> (specification 09 §6 step 4;
    /// FR-ARCH-004): positionally under <c>fixed-v1</c>, by content
    /// identifier across the whole prior version under <c>cdc-v1</c>. A
    /// reused segment emits its existing reference and writes no record.
    /// Reuse requires the segmentation profile and parameters to match
    /// exactly (09 §5); a mismatch re-archives in full, never mixes
    /// parameters.
    /// </summary>
    /// <remarks>
    /// The prior content identifiers come from the in-memory
    /// <see cref="ArchiveResult"/> — catalogue-domain data, never durable
    /// (specification 02 §2).
    /// </remarks>
    public async ValueTask<ArchiveResult> ArchiveAsync(Stream source, ArchiveResult? priorVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var usesCdc = _policy.SegmentationProfile == SegmentationProfile.CdcV1;

        // The reuse key (09 §5): content identifier + logical length +
        // segmentation profile + segmentation parameters, all matching.
        var comparable = priorVersion is not null &&
            priorVersion.SegmentationProfile == _policy.SegmentationProfile &&
            (usesCdc
                ? priorVersion.CdcParameters == _policy.CdcParameters
                : priorVersion.SegmentSize == _policy.SegmentSize);

        // cdc-v1 compares by content across the whole prior version — an
        // insertion shifts positions but not content (09 §3.2, §6).
        Dictionary<ContentId, (ObjectId ObjectId, long Length)>? priorByContent = null;
        if (comparable && usesCdc)
        {
            priorByContent = new Dictionary<ContentId, (ObjectId, long)>(priorVersion!.SegmentReferences.Count);
            for (var i = 0; i < priorVersion.SegmentReferences.Count; i++)
            {
                priorByContent.TryAdd(
                    priorVersion.SegmentContentIds[i],
                    (priorVersion.SegmentReferences[i].ObjectId, priorVersion.SegmentReferences[i].LogicalLength));
            }
        }

        var references = new List<SegmentReference>();
        var contentIds = new List<ContentId>();
        var blobs = new List<ArchivedBlob>();
        var classKey = _keys.DeriveClassKey(BlobClass.Data, _generation);
        var segmentBuffer = ArrayPool<byte>.Shared.Rent(_policy.MaximumSegmentBytes);
        var compressed = ArrayPool<byte>.Shared.Rent(_policy.MaximumSegmentBytes);
        using var objectIdDeriver = new ObjectIdDeriver(_keys.ContentIdKey);
        using var storeKeyDeriver = new StoreBlobKeyDeriver(_keys.KeyIdKey);
        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var codec = _policy.Compression.Profile == CompressionProfile.ZstdV1
            ? new ZstdSegmentCodec(_policy.Compression.ZstdLevel, _policy.MaximumSegmentBytes)
            : null;

        BlobWriter? writer = null;
        var logicalLength = 0L;

        try
        {
            ISegmentReader segmentReader = usesCdc
                ? new CdcSegmentReader(source, _policy.CdcParameters!.Value)
                : new FixedSegmentReader(source, _policy.SegmentSize);

            while (await segmentReader.ReadNextAsync(segmentBuffer, cancellationToken).ConfigureAwait(false) is { } segment)
            {
                var plaintext = segmentBuffer.AsMemory(0, segment.Length);
                logicalLength = segment.Offset + segment.Length;

                // 04 §5 order: content id, object id, compress, ordinal, encrypt.
                var contentId = ContentHasher.Hash(plaintext.Span);
                wholeFile.AppendData(plaintext.Span);

                // Content-based reuse (09 §6, cdc-v1): the same bytes anywhere
                // in the prior version need no new record.
                if (priorByContent is not null &&
                    priorByContent.TryGetValue(contentId, out var prior) &&
                    prior.Length == segment.Length)
                {
                    references.Add(new SegmentReference(segment.Offset, segment.Length, prior.ObjectId));
                    contentIds.Add(contentId);
                    continue;
                }

                // Positional reuse (09 §6, fixed-v1): identical content at the
                // same position of the prior version needs no new record — the
                // existing object already carries these bytes.
                if (comparable && !usesCdc &&
                    segment.Index < priorVersion!.SegmentReferences.Count &&
                    priorVersion.SegmentReferences[(int)segment.Index].LogicalLength == segment.Length &&
                    priorVersion.SegmentContentIds[(int)segment.Index] == contentId)
                {
                    references.Add(new SegmentReference(
                        segment.Offset,
                        segment.Length,
                        priorVersion.SegmentReferences[(int)segment.Index].ObjectId));
                    contentIds.Add(contentId);
                    continue;
                }

                var objectId = objectIdDeriver.Derive(ObjectType.SegmentRecord, contentId);

                var payload = plaintext;
                var profile = CompressionProfile.None;
                if (codec is not null &&
                    codec.TryCompressForStorage(plaintext.Span, _policy.Compression.ThresholdPermille, compressed, out var written))
                {
                    payload = compressed.AsMemory(0, written);
                    profile = CompressionProfile.ZstdV1;
                }

                if (writer is not null && !writer.CanAppend(payload.Length))
                {
                    await SealAndUploadAsync(writer, storeKeyDeriver, blobs, cancellationToken).ConfigureAwait(false);
                    writer = null;
                }

                writer ??= BlobWriter.Create(
                    _repositoryId,
                    _writerId,
                    _generation,
                    BlobClass.Data,
                    classKey,
                    _counters.AllocateNext(),
                    _policy.EncryptionProfile,
                    _policy.BlobWriteProfile,
                    _spoolDirectory,
                    pinned: _pinned);

                await writer.AppendRecordAsync(
                    ObjectType.SegmentRecord,
                    objectId,
                    profile,
                    (ulong)segment.Length,
                    payload,
                    cancellationToken).ConfigureAwait(false);

                references.Add(new SegmentReference(segment.Offset, segment.Length, objectId));
                contentIds.Add(contentId);

                if (writer.ShouldSeal)
                {
                    await SealAndUploadAsync(writer, storeKeyDeriver, blobs, cancellationToken).ConfigureAwait(false);
                    writer = null;
                }
            }

            if (writer is not null)
            {
                // Job completion seals the open blob (specification 05 §5).
                await SealAndUploadAsync(writer, storeKeyDeriver, blobs, cancellationToken).ConfigureAwait(false);
                writer = null;
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            CryptographicOperations.ZeroMemory(classKey);
            ArrayPool<byte>.Shared.Return(segmentBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(compressed, clearArray: true);
        }

        var hash = new byte[32];
        wholeFile.GetHashAndReset(hash);

        return new ArchiveResult(
            references,
            contentIds,
            blobs,
            logicalLength,
            hash,
            _policy.SegmentationProfile,
            _policy.SegmentSize,
            _policy.CdcParameters);
    }

    private async ValueTask SealAndUploadAsync(
        BlobWriter writer,
        StoreBlobKeyDeriver storeKeyDeriver,
        List<ArchivedBlob> blobs,
        CancellationToken cancellationToken)
    {
        var sealedBlob = await writer.SealAsync(cancellationToken).ConfigureAwait(false);
        await using (sealedBlob.ConfigureAwait(false))
        {
            var storeBlobKey = storeKeyDeriver.Derive(sealedBlob.BlobId);
            var storeKey = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeBlobKey);

            // 08 §3.1: no blob is uploaded before an unretired intent naming
            // it is durable. The scope publishes the covering extension and
            // awaits durability before the put leaves this process.
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

            blobs.Add(new ArchivedBlob(
                sealedBlob.BlobId,
                storeBlobKey,
                storeKey,
                sealedBlob.Digest,
                sealedBlob.RecordTable.Count,
                sealedBlob.Length,
                sealedBlob.RecordTable));
        }

        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
