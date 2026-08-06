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

        var session = OpenSession();
        await using (session.ConfigureAwait(false))
        {
            var result = await session.ArchiveFileAsync(source, priorVersion, cancellationToken).ConfigureAwait(false);
            await session.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Single-file semantics: the result carries every blob the call
            // produced, exactly as before sessions existed.
            return new ArchiveResult(
                result.SegmentReferences,
                result.SegmentContentIds,
                session.Blobs.ToList(),
                result.LogicalLength,
                result.WholeFileHash,
                result.SegmentationProfile,
                result.SegmentSize,
                result.CdcParameters);
        }
    }

    /// <summary>
    /// Opens a session for archiving many files with blob continuity across
    /// file boundaries (specification 05 §5) — the multi-file publication
    /// path. <paramref name="segmentExists"/>, when supplied, answers
    /// whether an object identifier is already located by the index — the
    /// cross-snapshot segment-reuse test (09 §6). The caller flushes and
    /// disposes the session.
    /// </summary>
    public ArchiveSession OpenSession(Func<Domain.Identifiers.ObjectId, bool>? segmentExists = null) => new(
        _policy, _repositoryId, _writerId, _generation, _keys, _store, _counters, _spoolDirectory, _pinned, _intentScope,
        segmentExists);
}
