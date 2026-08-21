using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>The outcome of restoring one file version.</summary>
public sealed record RestoreResult
{
    internal RestoreResult(bool success, long length, IReadOnlyList<byte>? wholeFileHash, string? failureDetail)
    {
        Success = success;
        Length = length;
        WholeFileHash = wholeFileHash;
        FailureDetail = failureDetail;
    }

    /// <summary>Whether every segment restored and verified.</summary>
    public bool Success { get; }

    /// <summary>The restored length on success.</summary>
    public long Length { get; }

    /// <summary>SHA-256 over the restored plaintext, on success.</summary>
    public IReadOnlyList<byte>? WholeFileHash { get; }

    /// <summary>What failed, when restore refused.</summary>
    public string? FailureDetail { get; }
}

/// <summary>
/// One blob the load could not open, and why — reported, never silent
/// (specification 00 §3's posture applied per blob rather than per load).
/// </summary>
public sealed record SkippedBlob(ObjectKey Key, string Reason);

/// <summary>
/// The read side of the slice, built on footers alone (specification 05 §4,
/// 07 §10's premise; FR-ARCH-006, FR-MAN-007): list the blob namespace, open
/// each blob through its locator and recovery footer, index records by object
/// identifier, and restore file versions from logical segment references —
/// with no index objects and no catalogue anywhere in the path.
/// </summary>
/// <remarks>
/// Restore never emits a partial file (specification 04 §7; FR-RST-005): the
/// output spools privately and reaches the caller's destination only after
/// every segment has decrypted, decompressed, and content-verified.
/// </remarks>
public sealed class RepositoryReader : IDisposable
{
    private readonly RepositoryId _repositoryId;
    private readonly RepositoryKeySet _keys;
    private readonly IObjectStore _store;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly byte[]? _sealingPrivateKey;
    private readonly Func<BlobEnvelope, byte[]>? _sealedContentKeyOpener;
    private readonly List<BlobReader> _blobReaders = [];
    private readonly List<SkippedBlob> _skipped = [];
    private readonly Dictionary<ObjectId, (BlobReader Reader, RecordTableEntry Entry)> _records = [];
    private readonly ILogger _logger;

    /// <summary>Creates a reader; call <see cref="LoadBlobsAsync(CancellationToken)"/> (or the targeted overload) before reading.</summary>
    public RepositoryReader(
        RepositoryId repositoryId, RepositoryKeySet keys, IObjectStore store, ILogger? logger = null)
        : this(repositoryId, keys, store, readAuthority: null, logger)
    {
    }

    /// <summary>
    /// Creates a reader holding a restore grant (ADR-0042 §5): sealed v2
    /// data blobs' content keys open under
    /// <paramref name="readAuthority"/>'s derived scalar. Without one, a
    /// sealed blob's structure still loads and its record reads answer
    /// <see cref="RecordReadOutcome.ContentSealed"/>. The scalar is copied
    /// and zeroed on dispose; the authority stays the caller's to dispose.
    /// </summary>
    public RepositoryReader(
        RepositoryId repositoryId,
        RepositoryKeySet keys,
        IObjectStore store,
        RepositoryReadAuthority? readAuthority,
        ILogger? logger = null)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(store);

        _repositoryId = repositoryId;
        _keys = keys;
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);

        if (readAuthority is not null)
        {
            var sealingPrivateKey = readAuthority.SealingPrivateKey.ToArray();
            _sealingPrivateKey = sealingPrivateKey;
            _sealedContentKeyOpener = envelope =>
                SealedContentKey.Open(sealingPrivateKey, envelope.SealedContentKey, _repositoryId, envelope.BlobId);
        }
    }

    /// <summary>Every record-table entry across the loaded blobs.</summary>
    public IEnumerable<RecordTableEntry> AllRecords => _blobReaders.SelectMany(reader => reader.RecordTable);

    /// <summary>
    /// Each loaded blob with the records its footer declares — the sweep
    /// side's view (architecture 07 §3): a blob is deletable only when every
    /// record it holds is unreachable, and a record duplicated across blobs
    /// conservatively keeps each blob holding it. Skipped blobs are absent
    /// here and must never be swept — see <see cref="SkippedBlobs"/>.
    /// </summary>
    public IEnumerable<(ObjectKey StoreKey, BlobId BlobId, IReadOnlyList<RecordTableEntry> Records)> Blobs =>
        _blobReaders.Select(reader => (reader.StoreKey, reader.Envelope.BlobId, reader.RecordTable));

    /// <summary>
    /// The blobs the last load could not open, each with the refusal's own
    /// message. A skipped blob's records read as absent — every downstream
    /// caller already refuses a missing record loudly — and never as wrong
    /// bytes; naming the damage exhaustively is <c>verify</c>'s job.
    /// </summary>
    public IReadOnlyList<SkippedBlob> SkippedBlobs => _skipped;

    /// <summary>
    /// Lists the blob namespace and opens every blob through its recovery
    /// footer, indexing records by object identifier. Corruption is local
    /// (architecture 04 §7; NFR-REL-004): a blob that will not open is
    /// skipped and reported in <see cref="SkippedBlobs"/> rather than
    /// refusing the load — one torn orphan must not block every restore of
    /// every committed snapshot, which is the posture the recovery tool and
    /// the verify engine already hold.
    /// </summary>
    /// <returns>The number of blobs opened, skips excluded.</returns>
    public async ValueTask<int> LoadBlobsAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            BlobReader reader;
            try
            {
                reader = await BlobReader.OpenAsync(
                    _store,
                    entry.Key,
                    entry.Length,
                    _repositoryId,
                    _keys.DeriveClassKey,
                    _objectIdDeriver,
                    cancellationToken,
                    _sealedContentKeyOpener,
                    _logger).ConfigureAwait(false);
            }
            catch (BlobFormatException exception)
            {
                // Damage, scoped to the blob it is in. An IOException is not
                // caught here on purpose: a transient store fault is not a
                // damage finding, and treating it as one would silently
                // narrow the loaded world on a flaky connection.
                Log.BlobSkipped(_logger, entry.Key, exception.Message);
                _skipped.Add(new SkippedBlob(entry.Key, exception.Message));
                continue;
            }

            _blobReaders.Add(reader);

            foreach (var record in reader.RecordTable)
            {
                // First writer wins; duplicates across blobs are legitimate
                // (the same segment archived twice) and interchangeable.
                _records.TryAdd(record.ObjectId, (reader, record));
            }
        }

        Log.BlobsLoaded(_logger, _blobReaders.Count, _repositoryId, _skipped.Count);

        return _blobReaders.Count;
    }

    /// <summary>
    /// Opens only the named blobs — the targeted load (ADR-0041). The full
    /// load reads every blob footer in the store, which over a remote
    /// retrieval session would download footers for blobs the restore never
    /// touches; a caller that already knows the blob set a plan needs (the
    /// plan probe computes exactly that) hands it here instead. Skips and
    /// duplicate handling are identical to the full load; a named blob that
    /// is absent lands in <see cref="SkippedBlobs"/>, and its records read as
    /// missing downstream — loudly, as always.
    /// </summary>
    /// <returns>The number of blobs opened, skips excluded.</returns>
    public async ValueTask<int> LoadBlobsAsync(
        IReadOnlyCollection<ObjectKey> blobStoreKeys, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(blobStoreKeys);

        var opened = 0;
        foreach (var key in blobStoreKeys)
        {
            var metadata = await _store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (metadata.Metadata is not { } found)
            {
                _skipped.Add(new SkippedBlob(key, "the store does not hold this blob"));
                continue;
            }

            BlobReader reader;
            try
            {
                reader = await BlobReader.OpenAsync(
                    _store,
                    key,
                    found.Length,
                    _repositoryId,
                    _keys.DeriveClassKey,
                    _objectIdDeriver,
                    cancellationToken,
                    _sealedContentKeyOpener,
                    _logger).ConfigureAwait(false);
            }
            catch (BlobFormatException exception)
            {
                _skipped.Add(new SkippedBlob(key, exception.Message));
                continue;
            }

            _blobReaders.Add(reader);
            opened++;

            foreach (var record in reader.RecordTable)
            {
                _records.TryAdd(record.ObjectId, (reader, record));
            }
        }

        return opened;
    }

    /// <summary>
    /// Locates the blob and table entry serving <paramref name="objectId"/> —
    /// the targetable shape forensic recovery needs: one named object without
    /// a whole-repository scan (NFR-PERF-015's direction).
    /// </summary>
    public bool TryLocateRecord(ObjectId objectId, out ObjectKey blobStoreKey, out RecordTableEntry entry)
    {
        if (_records.TryGetValue(objectId, out var located))
        {
            blobStoreKey = located.Reader.StoreKey;
            entry = located.Entry;
            return true;
        }

        blobStoreKey = default;
        entry = default;
        return false;
    }

    /// <summary>
    /// Reads and verifies one segment by object identifier — the full
    /// specification 04 §6 sequence including step 7.
    /// </summary>
    public ValueTask<RecordReadResult> ReadSegmentAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(objectId, out var located))
        {
            return ValueTask.FromResult(RecordReadResult.Failure(
                RecordReadOutcome.FormatViolation,
                $"No loaded blob carries a record for object {objectId}."));
        }

        return located.Reader.ReadRecordAsync(located.Entry, cancellationToken);
    }

    /// <summary>
    /// Restores a file version from its logical segment references with no
    /// assembly check: every segment verifies individually, and the computed
    /// whole-file hash is returned for the <em>caller</em> to compare. Two
    /// same-length segments swapped pass every per-part check here — prefer
    /// the overload that takes the manifest's expected hash whenever the
    /// caller has one (architecture 08 §3).
    /// </summary>
    public ValueTask<RestoreResult> RestoreAsync(
        IReadOnlyList<SegmentReference> references,
        Stream destination,
        CancellationToken cancellationToken) =>
        RestoreAsync(references, destination, expectedContentHash: default, cancellationToken);

    /// <summary>
    /// Restores a file version from its logical segment references. Nothing
    /// reaches <paramref name="destination"/> unless every segment verifies —
    /// a failed record refuses the whole restore rather than emitting a
    /// partial file (FR-RST-005) — and, when
    /// <paramref name="expectedContentHash"/> is provided, unless the
    /// assembled whole-file hash matches it, which is the only check that
    /// catches verified segments assembled in the wrong order.
    /// </summary>
    public async ValueTask<RestoreResult> RestoreAsync(
        IReadOnlyList<SegmentReference> references,
        Stream destination,
        ReadOnlyMemory<byte> expectedContentHash,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(references);
        ThrowHelper.ThrowIfNull(destination);

        var spoolPath = Path.Combine(Path.GetTempPath(), $"fbp-restore-{Guid.NewGuid():n}.spool");

        try
        {
            using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var expectedOffset = 0L;

            var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
            await using (spool.ConfigureAwait(false))
            {
                foreach (var reference in references)
                {
                    if (reference.LogicalOffset != expectedOffset)
                    {
                        return new RestoreResult(false, 0, null,
                            $"Segment references must cover the file contiguously; expected offset {expectedOffset}, got {reference.LogicalOffset} (specification 06 §3.2).");
                    }

                    var result = await ReadSegmentAsync(reference.ObjectId, cancellationToken).ConfigureAwait(false);

                    if (result.Outcome != RecordReadOutcome.Ok)
                    {
                        // Refuse the whole file; nothing was written to the
                        // caller's destination.
                        return new RestoreResult(false, 0, null,
                            $"Segment at offset {reference.LogicalOffset}: {result.Outcome} — {result.Detail}");
                    }

                    if (result.Plaintext!.LongLength != reference.LogicalLength)
                    {
                        return new RestoreResult(false, 0, null,
                            $"Segment at offset {reference.LogicalOffset} restored {result.Plaintext.Length} bytes; the reference declares {reference.LogicalLength}.");
                    }

                    await spool.WriteAsync(result.Plaintext, cancellationToken).ConfigureAwait(false);
                    wholeFile.AppendData(result.Plaintext);
                    expectedOffset += reference.LogicalLength;
                }

                var hash = new byte[32];
                wholeFile.GetHashAndReset(hash);

                // The assembly check, before a byte reaches the caller: every
                // segment passed its own verification, so a mismatch here can
                // only mean verified content assembled into the wrong file.
                if (!expectedContentHash.IsEmpty && !expectedContentHash.Span.SequenceEqual(hash))
                {
                    return new RestoreResult(false, 0, null,
                        "The assembled content does not match the expected whole-file hash; every segment verified individually, so the reference list assembles a different file (architecture 08 §3).");
                }

                // Every segment verified: only now does anything reach the
                // caller's destination.
                spool.Position = 0;
                await spool.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

                return new RestoreResult(true, expectedOffset, hash, null);
            }
        }
        finally
        {
            if (File.Exists(spoolPath))
            {
                File.Delete(spoolPath);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var reader in _blobReaders)
        {
            reader.Dispose();
        }

        _objectIdDeriver.Dispose();

        if (_sealingPrivateKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sealingPrivateKey);
        }
    }
}
