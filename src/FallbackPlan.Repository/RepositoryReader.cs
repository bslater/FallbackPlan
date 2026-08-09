using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

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
    private readonly List<BlobReader> _blobReaders = [];
    private readonly Dictionary<ObjectId, (BlobReader Reader, RecordTableEntry Entry)> _records = [];

    /// <summary>Creates a reader; call <see cref="LoadBlobsAsync"/> before reading.</summary>
    public RepositoryReader(RepositoryId repositoryId, RepositoryKeySet keys, IObjectStore store)
    {
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(store);

        _repositoryId = repositoryId;
        _keys = keys;
        _store = store;
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
    }

    /// <summary>Every record-table entry across the loaded blobs.</summary>
    public IEnumerable<RecordTableEntry> AllRecords => _blobReaders.SelectMany(reader => reader.RecordTable);

    /// <summary>
    /// Lists the blob namespace and opens every blob through its recovery
    /// footer, indexing records by object identifier.
    /// </summary>
    /// <returns>The number of blobs opened.</returns>
    /// <exception cref="BlobFormatException">A blob is damaged — the finding names it.</exception>
    public async ValueTask<int> LoadBlobsAsync(CancellationToken cancellationToken)
    {
        await foreach (var entry in _store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var reader = await BlobReader.OpenAsync(
                _store,
                entry.Key,
                entry.Length,
                _repositoryId,
                _keys.DeriveClassKey,
                _objectIdDeriver,
                cancellationToken).ConfigureAwait(false);

            _blobReaders.Add(reader);

            foreach (var record in reader.RecordTable)
            {
                // First writer wins; duplicates across blobs are legitimate
                // (the same segment archived twice) and interchangeable.
                _records.TryAdd(record.ObjectId, (reader, record));
            }
        }

        return _blobReaders.Count;
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
    }
}
