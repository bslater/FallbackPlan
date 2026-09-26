using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
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
    private readonly SealedContentKeyOpener? _sealedContentKeyOpener;
    private readonly List<BlobReader> _blobReaders = [];
    private readonly List<SkippedBlob> _skipped = [];
    private readonly Dictionary<ObjectId, (BlobReader Reader, RecordTableEntry Entry)> _records = [];
    private readonly ILogger _logger;

    // The fast read (NFR-PERF-009). Opening a blob costs three ranged reads
    // before a byte of payload, so a path that opens every blob it reads from
    // cannot reach the budget however well it coalesces. A location source —
    // the catalogue — answers the offset, the stored length and the profiles,
    // and the blob's framing is one 88-byte envelope read held for the run.
    private readonly Dictionary<ObjectKey, BlobReader> _framing = [];
    private readonly Dictionary<BlobId, (ObjectKey Key, long Length)> _located = [];
    private readonly Dictionary<ObjectKey, List<BlobRun>> _prefetched = [];
    private readonly Queue<(ObjectKey Key, BlobRun Run)> _prefetchOrder = new();
    private long _held;
    private Func<ObjectId, ResolvedLocation?>? _locations;
    private StoreBlobKeyDeriver? _storeKeyDeriver;

    private PrefetchPolicy _prefetch = PrefetchPolicy.Default;

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
            // The opener copies the scalar and zeroes it on dispose, so the
            // authority stays the caller's and this reader holds no second
            // copy of its own.
            _sealedContentKeyOpener = new SealedContentKeyOpener(readAuthority.SealingPrivateKey, repositoryId);
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
    public async ValueTask<RecordReadResult> ReadSegmentAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        RecordsRead++;

        if (_locations is not null && _locations(objectId) is { } location)
        {
            var fast = await ReadFromLocationAsync(location, objectId, cancellationToken).ConfigureAwait(false);
            if (fast is { } read && read.Outcome == RecordReadOutcome.Ok)
            {
                return read;
            }

            // A location cache is never authoritative, so a fast read that
            // fails is a question rather than an answer: fall back to the
            // footer, which is the path built to say what is wrong with a
            // blob (architecture 04 §7; NFR-REL-004). Damage is still
            // diagnosed by the reader that knows how to describe it, and a
            // stale offset costs one wasted read rather than a lost file.
            LocationFallbacks++;
            await OpenForFallbackAsync(location, cancellationToken).ConfigureAwait(false);
        }

        if (!_records.TryGetValue(objectId, out var located))
        {
            return RecordReadResult.Failure(
                RecordReadOutcome.FormatViolation,
                $"No loaded blob carries a record for object {objectId}.");
        }

        return await located.Reader.ReadRecordAsync(located.Entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a blob actually is and how long it is, trying both classes —
    /// memoized, because every record in a blob asks the same question and
    /// the answer costs a metadata call. Null when the store holds neither.
    /// </summary>
    private async ValueTask<(ObjectKey Key, long Length)?> LocateBlobAsync(
        ResolvedLocation location, CancellationToken cancellationToken)
    {
        if (_located.TryGetValue(location.BlobId, out var known))
        {
            return known;
        }

        _storeKeyDeriver ??= new StoreBlobKeyDeriver(_keys.KeyIdKey);
        var blobKey = location.StoreBlobKey ?? _storeKeyDeriver.Derive(location.BlobId);

        foreach (var blobClass in new[] { BlobClass.Data, BlobClass.Metadata })
        {
            var storeKey = BlobStoreKeys.ForBlob(blobClass, blobKey);
            var metadata = await _store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
            if (metadata.Metadata is { Length: > 0 } found)
            {
                _located[location.BlobId] = (storeKey, found.Length);
                return (storeKey, found.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the blob a failed fast read named, through its locator and
    /// footer, so the footer path can answer — the lazy half of the
    /// fallback. A reader using a location source loads nothing up front,
    /// so without this there would be nothing to fall back <em>to</em>.
    /// </summary>
    private async ValueTask OpenForFallbackAsync(ResolvedLocation location, CancellationToken cancellationToken)
    {
        if (await LocateBlobAsync(location, cancellationToken).ConfigureAwait(false) is not { } blob)
        {
            return;
        }

        if (_blobReaders.Any(open => open.StoreKey == blob.Key))
        {
            return;
        }

        BlobReader reader;
        try
        {
            reader = await BlobReader.OpenAsync(
                _store, blob.Key, blob.Length, _repositoryId, _keys.DeriveClassKey, _objectIdDeriver,
                cancellationToken, _sealedContentKeyOpener, _logger).ConfigureAwait(false);
        }
        catch (BlobFormatException exception)
        {
            // Damage, scoped to the blob it is in — the same posture the
            // full load takes, so a fallback cannot turn one torn blob
            // into a failed restore of everything else.
            Log.BlobSkipped(_logger, blob.Key, exception.Message);
            _skipped.Add(new SkippedBlob(blob.Key, exception.Message));
            return;
        }

        _blobReaders.Add(reader);
        foreach (var record in reader.RecordTable)
        {
            _records.TryAdd(record.ObjectId, (reader, record));
        }
    }

    /// <summary>
    /// Reads a record straight from where a location source says it is: from
    /// a prefetched run when one covers it, otherwise one ranged read, plus
    /// one envelope read the first time the blob is touched. Answers null
    /// when the blob's framing cannot be opened at all, which the caller
    /// treats as a miss rather than as damage — the footer path decides that.
    /// </summary>
    private async ValueTask<RecordReadResult?> ReadFromLocationAsync(
        ResolvedLocation location, ObjectId objectId, CancellationToken cancellationToken)
    {
        if (await LocateBlobAsync(location, cancellationToken).ConfigureAwait(false) is not { } blob)
        {
            return null;
        }

        var framing = await EnsureFramingAsync(blob.Key, blob.Length, cancellationToken).ConfigureAwait(false);
        if (framing is null)
        {
            return null;
        }

        return await framing.ReadRecordAsync(
            new RecordSpan(
                objectId,
                location.PhysicalOffset,
                location.StoredLength,
                location.CompressionProfileValue,
                location.EncryptionProfileValue),
            RunCovering(
                blob.Key,
                (long)location.PhysicalOffset,
                // Clamped exactly as the prefetch clamped it, so the last
                // record in a blob is not asked for bytes past its end and
                // then declared uncovered.
                Math.Min(
                    blob.Length - (long)location.PhysicalOffset,
                    RecordFraming.MaxRecordLength(location.StoredLength))),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This blob's framing, opened once and held for the run: from a
    /// prefetched run that reaches offset 0 — the envelope folded into a read
    /// that was happening anyway — or from one 88-byte read of its own.
    /// </summary>
    private async ValueTask<BlobReader?> EnsureFramingAsync(
        ObjectKey storeKey, long blobLength, CancellationToken cancellationToken)
    {
        if (_framing.TryGetValue(storeKey, out var framing))
        {
            return framing;
        }

        var envelopeLength = BlobReader.EnvelopeReadLength(blobLength);

        try
        {
            framing = RunCovering(storeKey, 0, envelopeLength) is { } head
                && head.TrySlice(0, envelopeLength, out var envelope)
                ? BlobReader.OpenFramingFrom(
                    _store, storeKey, blobLength, _repositoryId, _keys.DeriveClassKey, _objectIdDeriver,
                    envelope, _sealedContentKeyOpener, _logger)
                : await BlobReader.OpenFramingAsync(
                    _store, storeKey, blobLength, _repositoryId, _keys.DeriveClassKey, _objectIdDeriver,
                    cancellationToken, _sealedContentKeyOpener, _logger).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BlobFormatException or IOException)
        {
            return null;
        }

        _framing[storeKey] = framing;
        return framing;
    }

    /// <summary>
    /// A prefetched run holding the whole of <paramref name="length"/> bytes
    /// at <paramref name="offset"/>, if one does. The length matters: a run
    /// that holds a record's first byte and not its last cannot serve it, and
    /// answering with one would turn a coalesced read into a truncated
    /// record — a damage finding about the blob, invented by the prefetch.
    /// </summary>
    private BlobRun? RunCovering(ObjectKey storeKey, long offset, long length)
    {
        if (!_prefetched.TryGetValue(storeKey, out var runs))
        {
            return null;
        }

        foreach (var run in runs)
        {
            if (offset >= run.Offset && offset + length <= run.End)
            {
                return run;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches, in as few ranged reads as the bounds allow, the records
    /// <paramref name="objectIds"/> names — the coalescing term of
    /// NFR-PERF-009. Records in one blob are grouped, sorted by offset and
    /// merged into runs under <see cref="PrefetchPolicy.CoalesceWindowBytes"/>
    /// and <see cref="PrefetchPolicy.MaximumBridgeBytes"/>; a run whose first
    /// record is close enough to the start reaches down to offset 0 and takes
    /// the envelope with it, which is the difference between paying two reads
    /// per blob and one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs are kept, not scoped to the call: a snapshot's files are
    /// prefetched one at a time and consecutive files share the blob they
    /// were written into, so a run released with its file would be fetched
    /// again by the next one — which is one read per (file, blob) pair rather
    /// than one per blob, and no budget at all. They are evicted oldest-first
    /// once <see cref="PrefetchPolicy.BudgetBytes"/> is reached, so what is
    /// held is bounded by configuration (NFR-PERF-001) rather than by the
    /// size of the plan.
    /// </para>
    /// <para>
    /// An optimisation that cannot fail a read: a record no run covers, a
    /// blob the store will not answer for, a run over the budget — each
    /// simply reads as it did before. Nothing is verified differently for
    /// having arrived in a bigger read. Without a location source there is
    /// nothing to prefetch from and this does nothing at all.
    /// </para>
    /// </remarks>
    /// <returns>How many bytes this call fetched.</returns>
    public async ValueTask<long> PrefetchAsync(
        IReadOnlyCollection<ObjectId> objectIds, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(objectIds);

        if (_locations is null)
        {
            return 0;
        }

        var fetched = 0L;

        // First-seen order, not the dictionary's: when a prefetch is larger
        // than the budget the runs taken are the ones wanted first, and the
        // rest read one at a time — which is only the right half to keep if
        // the order is the caller's.
        var wanted = new Dictionary<ObjectKey, (long BlobLength, List<(long Start, long End)> Spans)>();
        var order = new List<ObjectKey>();
        foreach (var objectId in objectIds.Distinct())
        {
            if (_locations(objectId) is not { } location)
            {
                continue;
            }

            if (await LocateBlobAsync(location, cancellationToken).ConfigureAwait(false) is not { } blob)
            {
                continue;
            }

            // Sized against the longest framing any container uses, because
            // the exact one is in the envelope this run may be about to
            // fetch. Over-fetching by at most 92 bytes a record beats a read
            // to find out.
            var start = (long)location.PhysicalOffset;
            var end = Math.Min(blob.Length, start + RecordFraming.MaxRecordLength(location.StoredLength));

            // Already in hand from an earlier file's prefetch, which is the
            // whole reason the runs outlive the call.
            if (RunCovering(blob.Key, start, end - start) is not null)
            {
                continue;
            }

            if (!wanted.TryGetValue(blob.Key, out var entry))
            {
                entry = (blob.Length, []);
                wanted[blob.Key] = entry;
                order.Add(blob.Key);
            }

            entry.Spans.Add((start, end));
        }

        foreach (var storeKey in order)
        {
            var (blobLength, spans) = wanted[storeKey];
            foreach (var run in PlanRuns(spans, blobLength, _framing.ContainsKey(storeKey), _prefetch))
            {
                // One call never fetches more than the budget, so a caller
                // that asks for more than can be held gets the front of what
                // it asked for rather than a buffer that evicts itself.
                if (run.End - run.Start > _prefetch.BudgetBytes || fetched >= _prefetch.BudgetBytes)
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = await BlobReader.ReadRangeAsync(
                        _store, storeKey, run.Start, run.End - run.Start, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is BlobFormatException or IOException)
                {
                    // A prefetch that cannot be served is not a finding: the
                    // records read one at a time and the footer path still
                    // has the last word on whether this blob is damaged.
                    continue;
                }

                Hold(storeKey, new BlobRun(run.Start, bytes));
                PrefetchedRuns++;
                PrefetchedBytes += bytes.Length;
                fetched += bytes.Length;
            }
        }

        return fetched;
    }

    /// <summary>Keeps one run, evicting the oldest until the budget is met.</summary>
    private void Hold(ObjectKey storeKey, BlobRun run)
    {
        if (!_prefetched.TryGetValue(storeKey, out var runs))
        {
            runs = [];
            _prefetched[storeKey] = runs;
        }

        runs.Add(run);
        _prefetchOrder.Enqueue((storeKey, run));
        _held += run.Bytes.Length;

        while (_held > _prefetch.BudgetBytes && _prefetchOrder.Count > 1)
        {
            var (oldestKey, oldest) = _prefetchOrder.Dequeue();
            if (_prefetched.TryGetValue(oldestKey, out var held))
            {
                held.Remove(oldest);
                if (held.Count == 0)
                {
                    _prefetched.Remove(oldestKey);
                }
            }

            _held -= oldest.Bytes.Length;
        }
    }

    /// <summary>
    /// The runs one blob's wanted spans become: sorted, merged while the gap
    /// is bridgeable and the result fits the window, and — when the blob's
    /// framing is not yet open — the first run extended down to offset 0 on
    /// the same two tests, so the envelope rides a read that was happening
    /// anyway.
    /// </summary>
    private static List<(long Start, long End)> PlanRuns(
        List<(long Start, long End)> spans, long blobLength, bool framed, PrefetchPolicy policy)
    {
        spans.Sort((left, right) => left.Start.CompareTo(right.Start));

        List<(long Start, long End)> runs = [];
        foreach (var span in spans)
        {
            if (runs.Count == 0)
            {
                runs.Add(span);
                continue;
            }

            var current = runs[^1];
            if (span.End <= current.End)
            {
                continue;
            }

            // A lone record over the window is still one run — there is no
            // smaller read that would serve it — but two records are never
            // merged into one that exceeds it.
            if (span.Start - current.End <= policy.MaximumBridgeBytes &&
                span.End - current.Start <= policy.CoalesceWindowBytes)
            {
                runs[^1] = (current.Start, span.End);
                continue;
            }

            runs.Add(span);
        }

        if (!framed && runs.Count > 0)
        {
            var first = runs[0];
            var envelope = BlobReader.EnvelopeReadLength(blobLength);
            if (first.Start > 0 &&
                first.Start - envelope <= policy.MaximumBridgeBytes &&
                first.End <= policy.CoalesceWindowBytes)
            {
                runs[0] = (0, first.End);
            }
        }

        return runs;
    }

    /// <summary>
    /// The bounds a prefetch coalesces under. Set before restoring when the
    /// defaults do not suit; <see cref="PrefetchPolicy"/> says what each one
    /// is protecting.
    /// </summary>
    public void UsePrefetchPolicy(PrefetchPolicy policy)
    {
        ThrowHelper.ThrowIfNull(policy);
        _prefetch = policy;
    }

    /// <summary>
    /// Whether this reader answers from a location source, and so has
    /// anything to coalesce. A caller that reads ahead to give the prefetch
    /// something to group asks this first: without a source there is nothing
    /// to group, and reading ahead would only move work earlier.
    /// </summary>
    public bool ReadsFromLocations => _locations is not null;

    /// <summary>The bounds a prefetch is coalescing under.</summary>
    public PrefetchPolicy Prefetch => _prefetch;

    /// <summary>How many coalesced reads this reader has issued.</summary>
    public int PrefetchedRuns { get; private set; }

    /// <summary>How many bytes those reads fetched.</summary>
    public long PrefetchedBytes { get; private set; }

    /// <summary>How many records this reader has been asked for.</summary>
    public long RecordsRead { get; private set; }

    /// <summary>
    /// Reads records straight from the locations <paramref name="resolver"/>
    /// answers, instead of from a loaded footer — the catalogue's
    /// <c>ResolveLocation</c> at every call site (NFR-PERF-009).
    /// </summary>
    /// <remarks>
    /// A reader given one need not load at all: loading is what the fast path
    /// exists to avoid, and a blob is opened through its footer only when a
    /// fast read fails. A caller that loads anyway keeps the footer path as
    /// its fallback, which is what the corruption cases rely on.
    /// </remarks>
    public void UseLocationSource(Func<ObjectId, ResolvedLocation?> resolver)
    {
        ThrowHelper.ThrowIfNull(resolver);
        _locations = resolver;
    }

    /// <summary>How many blobs have had their framing opened, one read each.</summary>
    public int FramedBlobs => _framing.Count;

    /// <summary>How many reads the location source could not satisfy and the footer path answered.</summary>
    public int LocationFallbacks { get; private set; }

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

            // The whole reference list is in hand before the loop, so the
            // records that sit next to each other in one blob are fetched
            // together rather than one read at a time (NFR-PERF-009).
            await PrefetchAsync(
                [.. references.Select(reference => reference.ObjectId)], cancellationToken).ConfigureAwait(false);

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

        foreach (var reader in _framing.Values)
        {
            reader.Dispose();
        }

        _storeKeyDeriver?.Dispose();
        _objectIdDeriver.Dispose();
        _sealedContentKeyOpener?.Dispose();
    }
}
