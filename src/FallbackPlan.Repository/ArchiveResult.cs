using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>One blob the archiver sealed and uploaded.</summary>
public sealed record ArchivedBlob(
    BlobId BlobId,
    StoreBlobKey StoreBlobKey,
    ObjectKey StoreKey,
    IReadOnlyList<byte> Digest,
    int RecordCount,
    long Length);

/// <summary>
/// The outcome of archiving one file version: the ordered logical segment
/// references (the future manifest's content, specification 06 §3), the
/// blobs written, and — in memory only — each segment's content identifier
/// and the segmentation parameters, which is what positional prior-version
/// comparison consumes (specification 09 §5–§6). Content identifiers never
/// reach a durable object (specification 02 §2); this type is
/// catalogue-domain data.
/// </summary>
public sealed class ArchiveResult
{
    internal ArchiveResult(
        IReadOnlyList<SegmentReference> segmentReferences,
        IReadOnlyList<ContentId> segmentContentIds,
        IReadOnlyList<ArchivedBlob> blobs,
        long logicalLength,
        IReadOnlyList<byte> wholeFileHash,
        SegmentationProfile segmentationProfile,
        SegmentSize segmentSize,
        CdcParameters? cdcParameters)
    {
        SegmentReferences = segmentReferences;
        SegmentContentIds = segmentContentIds;
        Blobs = blobs;
        LogicalLength = logicalLength;
        WholeFileHash = wholeFileHash;
        SegmentationProfile = segmentationProfile;
        SegmentSize = segmentSize;
        CdcParameters = cdcParameters;
    }

    /// <summary>The ordered logical segment references.</summary>
    public IReadOnlyList<SegmentReference> SegmentReferences { get; }

    /// <summary>
    /// Each segment's content identifier, parallel to
    /// <see cref="SegmentReferences"/>. In-memory only — never durable.
    /// </summary>
    public IReadOnlyList<ContentId> SegmentContentIds { get; }

    /// <summary>The blobs sealed and uploaded for this version.</summary>
    public IReadOnlyList<ArchivedBlob> Blobs { get; }

    /// <summary>The file's logical length.</summary>
    public long LogicalLength { get; }

    /// <summary>SHA-256 over the whole plaintext file.</summary>
    public IReadOnlyList<byte> WholeFileHash { get; }

    /// <summary>The segmentation profile this version used.</summary>
    public SegmentationProfile SegmentationProfile { get; }

    /// <summary>The segment size this version used (fixed-v1).</summary>
    public SegmentSize SegmentSize { get; }

    /// <summary>The cdc parameters this version used (cdc-v1); part of the reuse key (specification 09 §5).</summary>
    public CdcParameters? CdcParameters { get; }

    /// <summary>The number of records this archive run wrote.</summary>
    public int RecordsWritten => Blobs.Sum(blob => blob.RecordCount);
}
