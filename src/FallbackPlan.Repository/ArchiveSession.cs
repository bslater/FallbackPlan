using Bodu;
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Segmentation;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>A single-segment record written for an alternate stream (ADR-0026 §Decision 5).</summary>
public sealed record SingleSegmentRecord(ObjectId ObjectId, ContentId ContentId, ulong Length);

/// <summary>
/// One archiving session spanning many files: the specification 04 §5
/// pipeline per segment, with <b>blob continuity across files</b> — a blob
/// seals at the policy's targets, never at a file boundary, so a thousand
/// small files do not become a thousand tiny blobs (specification 05 §5:
/// job completion, not file completion, seals the open blob). Created by
/// <see cref="FileArchiver.OpenSession"/>; owned buffers and keys live for
/// the session and are zeroed on disposal.
/// </summary>
public sealed class ArchiveSession : IAsyncDisposable
{
    private static readonly byte[] ZeroBlock = new byte[64 * 1024];

    private readonly CapturePolicy _policy;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyGeneration _generation;
    private readonly IObjectStore _store;
    private readonly IBlobCounterAllocator _counters;
    private readonly string _spoolDirectory;
    private readonly SpoolPinnedConfiguration _pinned;
    private readonly IIntentScope? _intentScope;
    private readonly byte[] _classKey;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly StoreBlobKeyDeriver _storeKeyDeriver;

    // Codecs are pooled rather than shared or made per-segment. One wraps a
    // native compressor context and documents itself as not thread-safe, so the
    // concurrent stage cannot share one; and construction allocates that
    // context, so building one per segment would trade a lock for an
    // allocation. The bag holds at most as many as can be in flight.
    private readonly ConcurrentBag<ZstdSegmentCodec> _codecs = [];

    // Segment buffers, pooled per session rather than through
    // ArrayPool<byte>.Shared. Shared stops pooling above 1 MiB and a cdc-v1
    // segment can be 8 MiB, so renting one per segment from it allocated a
    // fresh large array every time — measured, and materially worse than the
    // single session-long buffer it replaced. ArrayPool.Create was worse again:
    // its implementation takes a lock per rent. A bag of same-sized buffers is
    // what this actually needs, and it holds no more than can be in flight.
    private readonly ConcurrentBag<byte[]> _buffers = [];
    private readonly List<ArchivedBlob> _blobs = [];

    // Upload leaves the archive loop (ADR-0029 §2). A sealed blob is handed to
    // a bounded set of workers and the loop continues; before this, the whole
    // pipeline stalled for the duration of every PUT — the largest structural
    // stall in the design, and worst precisely where it matters most, on a slow
    // or remote destination.
    private readonly Channel<SealedBlob> _uploads;
    private readonly Task[] _uploadWorkers;
    private readonly Lock _blobGate = new();
    private readonly ReusePredicate? _mayReuseSegment;

    // A reservation set, not a record of what was written. The concurrent stage
    // claims an object id before compressing it, so a duplicate segment loses
    // the race and is treated as reused rather than compressed and appended a
    // second time. ADR-0029 §1 frees which duplicate wins, but not how many
    // records get written, and the reuse suites assert exact record counts.
    private readonly ConcurrentDictionary<ObjectId, byte> _writtenThisSession = [];
    private BlobWriter? _writer;
    private bool _resumeAttempted;

    internal ArchiveSession(
        CapturePolicy policy,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyGeneration generation,
        RepositoryKeySet keys,
        IObjectStore store,
        IBlobCounterAllocator counters,
        string spoolDirectory,
        SpoolPinnedConfiguration pinned,
        IIntentScope? intentScope,
        ReusePredicate? mayReuseSegment)
    {
        _mayReuseSegment = mayReuseSegment;
        _policy = policy;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _generation = generation;
        _store = store;
        _counters = counters;
        _spoolDirectory = spoolDirectory;
        _pinned = pinned;
        _intentScope = intentScope;
        _classKey = keys.DeriveClassKey(BlobClass.Data, generation);
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        _storeKeyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);

        // Bounded by the one concurrency setting (ADR-0029 §3), so the memory
        // bound stays statable: blobs in flight are part of what NFR-PERF-001
        // counts. A full channel back-pressures the archive loop rather than
        // queueing without limit.
        // Capacity is concurrency + 1, not concurrency. At 1 they would be in
        // lock-step — the archive loop blocked handing over while the single
        // worker uploaded — which measured *slower* than the inline upload it
        // replaced. One slot of slack is what makes the hand-off a hand-off.
        // The memory bound stays statable: blobs in flight are bounded, and by
        // a number the setting still names.
        _uploads = Channel.CreateBounded<SealedBlob>(new BoundedChannelOptions(policy.Concurrency + 1)
        {
            SingleReader = false,
            SingleWriter = true,
        });

        _uploadWorkers = [.. Enumerable.Range(0, policy.Concurrency).Select(_ => Task.Run(UploadWorkerAsync))];
    }

    /// <summary>Every blob sealed and uploaded by this session so far.</summary>
    /// <remarks>
    /// A snapshot, not a view. Upload workers append to the underlying list
    /// while the archive loop runs (ADR-0029 §2), and handing out the live list
    /// would let a caller enumerate it during a resize.
    /// </remarks>
    public IReadOnlyList<ArchivedBlob> Blobs
    {
        get
        {
            lock (_blobGate)
            {
                return [.. _blobs];
            }
        }
    }

    /// <summary>The blobs sealed since <paramref name="sealedBefore"/>, and the count now.</summary>
    /// <remarks>
    /// One lock acquisition for both, because taking the count and the tail
    /// separately would let an upload land between them and attribute a blob to
    /// the wrong call.
    /// </remarks>
    private List<ArchivedBlob> BlobsSealedSince(int sealedBefore)
    {
        lock (_blobGate)
        {
            return [.. _blobs.Skip(sealedBefore)];
        }
    }

    private int SealedBlobCount()
    {
        lock (_blobGate)
        {
            return _blobs.Count;
        }
    }

    /// <summary>
    /// Whether an existing segment record may be referenced instead of written
    /// again (09 §5–§6; NFR-PERF-010; FR-DED-002).
    /// </summary>
    /// <remarks>
    /// The predicate answers with the index <em>and</em> the configured trust
    /// domain, so under the default it may perform a read before saying yes
    /// (<see cref="DedupTrustGate"/>). It is called from the concurrent stage
    /// on several threads at once and owns whatever serialisation its state
    /// needs — the session no longer holds a lock across it, because a lock
    /// held across a verification fetch would serialise the pipeline behind
    /// the network.
    /// </remarks>
    private ValueTask<bool> MayReuseSegmentAsync(ObjectId objectId, CancellationToken cancellationToken) =>
        _mayReuseSegment is null
            ? ValueTask.FromResult(false)
            : _mayReuseSegment(objectId, cancellationToken);

    /// <summary>
    /// Archives one file as a new version, comparing against
    /// <paramref name="priorVersion"/> exactly as
    /// <see cref="FileArchiver.ArchiveAsync(Stream, ArchiveResult?, CancellationToken)"/>
    /// documents. The returned result's <see cref="ArchiveResult.Blobs"/>
    /// holds only the blobs sealed <em>during this call</em> — a blob still
    /// open when the call returns is attributed to the call that seals it;
    /// <see cref="Blobs"/> aggregates the session.
    /// </summary>
    public async ValueTask<ArchiveResult> ArchiveFileAsync(
        Stream source, ArchiveResult? priorVersion, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);

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
        var sealedBefore = SealedBlobCount();
        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        ISegmentReader segmentReader = usesCdc
            ? new CdcSegmentReader(source, _policy.CdcParameters!.Value)
            : new FixedSegmentReader(source, _policy.SegmentSize);

        var logicalLength = 0L;

        // The staged pipeline (ADR-0029 §1). Each entry is a segment already in
        // flight; the channel's FIFO order is the reorder buffer, so the barrier
        // below sees segments in the order the reader produced them without
        // having to reconstruct that order. Capacity bounds how many are in
        // flight, and with it the memory NFR-PERF-001 bounds — the +1 is the
        // slack the upload channel's comment explains.
        var prepared = Channel.CreateBounded<Task<PreparedSegment>>(
            new BoundedChannelOptions(_policy.Concurrency + 1)
            {
                SingleReader = true,
                SingleWriter = true,
            });

        // The barrier runs on its own task and is the only thread that touches
        // the blob writer, the counter allocator and the ordinal sequence.
        var barrier = Task.Run(
            () => DrainPreparedAsync(prepared.Reader, references, contentIds, cancellationToken),
            CancellationToken.None);

        try
        {
            var reuse = new ReuseContext(priorByContent, comparable && !usesCdc ? priorVersion : null);

            while (true)
            {
                // Rented per segment, not per session: the reader fills the head
                // of whatever buffer it is given, so one shared buffer would be
                // overwritten by the next read while the previous segment was
                // still being compressed and appended.
                var plaintext = RentBuffer();
                SegmentDescriptor? read;
                try
                {
                    read = await segmentReader.ReadNextAsync(plaintext, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ReturnBuffer(plaintext);
                    throw;
                }

                if (read is not { } segment)
                {
                    ReturnBuffer(plaintext);
                    break;
                }

                logicalLength = segment.Offset + segment.Length;
                EngineDiagnostics.ArchiveBytesLogical.Add(segment.Length);

                // The whole-file hash covers the file in order, so it is fed
                // here and nowhere else. It is also the one SHA-256 pass that
                // stays serial; the per-segment content id moves below, which
                // is the "second hash" ADR-0029 §6 step 2 names.
                wholeFile.AppendData(plaintext.AsSpan(0, segment.Length));

                await prepared.Writer.WriteAsync(
                    PrepareSegmentAsync(segment, plaintext, reuse, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            prepared.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            prepared.Writer.TryComplete(exception);
        }

        // Always awaited, including when the producer failed: the barrier owns
        // returning every rented buffer, so abandoning it would leak them.
        await barrier.ConfigureAwait(false);

        var hash = new byte[32];
        wholeFile.GetHashAndReset(hash);

        return new ArchiveResult(
            references,
            contentIds,
            BlobsSealedSince(sealedBefore),
            logicalLength,
            hash,
            _policy.SegmentationProfile,
            _policy.SegmentSize,
            _policy.CdcParameters);
    }

    /// <summary>What a segment can be compared against for reuse (09 §6).</summary>
    /// <remarks>
    /// Both members are read-only for the file's duration, which is what lets
    /// the reuse tests run in the concurrent stage: they need the content id,
    /// and the content id is what moved off the serial path.
    /// </remarks>
    private readonly record struct ReuseContext(
        Dictionary<ContentId, (ObjectId ObjectId, long Length)>? ByContent,
        ArchiveResult? Positional);

    /// <summary>
    /// A segment carried from the concurrent stage to the ordered barrier.
    /// </summary>
    /// <remarks>
    /// It owns its rented buffers. The barrier returns them once it has either
    /// appended the record or decided there is none to append — no other path
    /// may return them, or one segment's buffer would be handed out while
    /// another still held a span into it.
    /// </remarks>
    private sealed record PreparedSegment(
        long Offset,
        int Length,
        ContentId ContentId,
        ObjectId ObjectId,
        byte[] Plaintext,
        byte[]? Compressed,
        int PayloadLength,
        CompressionProfile Profile,
        bool NeedsRecord);

    /// <summary>
    /// The concurrent stage: content id, reuse, object id, dedup reservation and
    /// compression. Everything here may run on any thread and in any order.
    /// </summary>
    private async Task<PreparedSegment> PrepareSegmentAsync(
        SegmentDescriptor segment, byte[] plaintext, ReuseContext reuse, CancellationToken cancellationToken)
    {
        // Yield first so the producer is not the thread that does this work —
        // otherwise the pipeline is a channel with a serial loop behind it.
        await Task.Yield();

        try
        {
            var contentId = ContentHasher.Hash(plaintext.AsSpan(0, segment.Length));

            // Content-based reuse (09 §6, cdc-v1): the same bytes anywhere in
            // the prior version need no new record.
            if (reuse.ByContent is not null &&
                reuse.ByContent.TryGetValue(contentId, out var prior) &&
                prior.Length == segment.Length)
            {
                return Reused(segment, plaintext, contentId, prior.ObjectId);
            }

            // Positional reuse (09 §6, fixed-v1): identical content at the same
            // position of the prior version needs no new record.
            if (reuse.Positional is { } positional &&
                segment.Index < positional.SegmentReferences.Count &&
                positional.SegmentReferences[(int)segment.Index].LogicalLength == segment.Length &&
                positional.SegmentContentIds[(int)segment.Index] == contentId)
            {
                return Reused(
                    segment, plaintext, contentId, positional.SegmentReferences[(int)segment.Index].ObjectId);
            }

            var objectId = _objectIdDeriver.Derive(ObjectType.SegmentRecord, contentId);

            // Segment reuse by object identifier (09 §6; NFR-PERF-010). The
            // in-session half is a reservation: whoever claims the id writes the
            // record, and anyone else with the same content is reused. Claiming
            // before compressing is what keeps the record count deterministic
            // when two identical segments are in flight at once.
            if (await MayReuseSegmentAsync(objectId, cancellationToken).ConfigureAwait(false) ||
                !_writtenThisSession.TryAdd(objectId, 0))
            {
                return Reused(segment, plaintext, contentId, objectId);
            }

            var codec = RentCodec();
            byte[]? compressed = null;
            var payloadLength = segment.Length;
            var profile = CompressionProfile.None;

            try
            {
                if (codec is not null)
                {
                    var destination = RentBuffer();
                    if (codec.TryCompressForStorage(
                            plaintext.AsSpan(0, segment.Length),
                            _policy.Compression.ThresholdPermille,
                            destination,
                            out var written))
                    {
                        compressed = destination;
                        payloadLength = written;
                        profile = CompressionProfile.ZstdV1;
                    }
                    else
                    {
                        ReturnBuffer(destination);
                    }
                }
            }
            finally
            {
                ReturnCodec(codec);
            }

            return new PreparedSegment(
                segment.Offset,
                segment.Length,
                contentId,
                objectId,
                plaintext,
                compressed,
                payloadLength,
                profile,
                NeedsRecord: true);
        }
        catch
        {
            // The buffer belongs to this method once it is handed over, so a
            // failure here returns it rather than leaving it to a barrier that
            // will never see this item.
            ReturnBuffer(plaintext);
            throw;
        }
    }

    private static PreparedSegment Reused(
        SegmentDescriptor segment, byte[] plaintext, ContentId contentId, ObjectId objectId)
    {
        EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "true"));

        return new PreparedSegment(
            segment.Offset,
            segment.Length,
            contentId,
            objectId,
            plaintext,
            Compressed: null,
            PayloadLength: 0,
            CompressionProfile.None,
            NeedsRecord: false);
    }

    /// <summary>
    /// The ordered barrier (ADR-0029 §1): assign ordinal, encrypt, append,
    /// digest — strictly in the order the reader produced the segments.
    /// </summary>
    /// <remarks>
    /// Single-threaded by construction, which is what every invariant below it
    /// rests on. <see cref="BlobWriter.AppendRecordAsync"/> takes its ordinal
    /// from the record count and is not re-entrant; the ordinal is the AEAD
    /// nonce, so two callers racing to one ordinal is the failure specification
    /// 05 §6.1 calls catastrophic. The blob counter allocator and the upload
    /// channel's <c>SingleWriter</c> both depend on this too.
    /// </remarks>
    private async Task DrainPreparedAsync(
        ChannelReader<Task<PreparedSegment>> reader,
        List<SegmentReference> references,
        List<ContentId> contentIds,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;

        await foreach (var pending in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            PreparedSegment segment;
            try
            {
                segment = await pending.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Keep draining. The producer blocks on a full channel, so
                // abandoning the loop here would deadlock it, and every item
                // still queued is holding rented buffers.
                failure ??= exception;
                continue;
            }

            try
            {
                if (failure is null)
                {
                    await AppendPreparedAsync(segment, references, contentIds, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                ReturnBuffer(segment.Plaintext);
                if (segment.Compressed is not null)
                {
                    ReturnBuffer(segment.Compressed);
                }
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private async ValueTask AppendPreparedAsync(
        PreparedSegment segment,
        List<SegmentReference> references,
        List<ContentId> contentIds,
        CancellationToken cancellationToken)
    {
        if (segment.NeedsRecord)
        {
            var payload = segment.Compressed is null
                ? segment.Plaintext.AsMemory(0, segment.Length)
                : segment.Compressed.AsMemory(0, segment.PayloadLength);

            if (_writer is not null && !_writer.CanAppend(payload.Length))
            {
                await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
            }

            _writer ??= OpenWriter();

            await _writer.AppendRecordAsync(
                ObjectType.SegmentRecord,
                segment.ObjectId,
                segment.Profile,
                (ulong)segment.Length,
                payload,
                cancellationToken).ConfigureAwait(false);

            EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "false"));
            EngineDiagnostics.ArchiveBytesStored.Add(payload.Length);

            if (_writer.ShouldSeal)
            {
                await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        references.Add(new SegmentReference(segment.Offset, segment.Length, segment.ObjectId));
        contentIds.Add(segment.ContentId);
    }

    /// <summary>A buffer big enough for any segment this policy can produce.</summary>
    private byte[] RentBuffer() =>
        _buffers.TryTake(out var buffer) ? buffer : new byte[_policy.MaximumSegmentBytes];

    /// <summary>
    /// Returns a segment buffer for reuse within this session.
    /// </summary>
    /// <remarks>
    /// Deliberately not cleared here. These buffers hold plaintext, and the
    /// session-long ones they replaced were zeroed on return because they went
    /// back to a pool other components draw from. This bag is private to one
    /// session and hands buffers back only to the same pipeline, so zeroing on
    /// every return would be an 8 MiB memset per segment that protects nothing.
    /// They are zeroed once, at disposal, before they become garbage.
    /// </remarks>
    private void ReturnBuffer(byte[] buffer) => _buffers.Add(buffer);

    private ZstdSegmentCodec? RentCodec() =>
        _policy.Compression.Profile != CompressionProfile.ZstdV1
            ? null
            : _codecs.TryTake(out var codec)
                ? codec
                : new ZstdSegmentCodec(_policy.Compression.ZstdLevel, _policy.MaximumSegmentBytes);

    private void ReturnCodec(ZstdSegmentCodec? codec)
    {
        if (codec is not null)
        {
            _codecs.Add(codec);
        }
    }

    /// <summary>
    /// Archives a sparse file: only the data runs between
    /// <paramref name="holes"/> are read and stored; the whole-file hash
    /// covers the materialised form, zeroes included (specification 06 §4.2),
    /// so the references plus the holes tile <c>[0, logical_length)</c>
    /// exactly as 06 §3.2 requires. Holes must be ascending,
    /// non-overlapping, and inside <c>[0, logicalLength)</c> — the scanner's
    /// contract; a violation falls back to dense archiving, because sparse
    /// capture is an optimisation and never a correctness input.
    /// </summary>
    public async ValueTask<ArchiveResult> ArchiveSparseFileAsync(
        Stream source, IReadOnlyList<SparseExtent> holes, long logicalLength, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(holes);

        if (!HolesAreWellFormed(holes, logicalLength))
        {
            var dense = await ArchiveFileAsync(source, priorVersion: null, cancellationToken).ConfigureAwait(false);
            return dense;
        }

        var references = new List<SegmentReference>();
        var contentIds = new List<ContentId>();
        var sealedBefore = SealedBlobCount();
        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long position = 0;
        var holeIndex = 0;

        // One rental for the call. This path is serial by nature — a run's
        // segments are read and appended one at a time — so nothing else can be
        // holding a span into the buffer when the next read fills it.
        var segmentBuffer = RentBuffer();
        try
        {
        while (position < logicalLength)
        {
            if (holeIndex < holes.Count && (long)holes[holeIndex].Offset == position)
            {
                // A hole: zeroes into the hash, nothing into storage.
                var remaining = (long)holes[holeIndex].Length;
                while (remaining > 0)
                {
                    var block = (int)Math.Min(remaining, ZeroBlock.Length);
                    wholeFile.AppendData(ZeroBlock.AsSpan(0, block));
                    remaining -= block;
                }

                position += (long)holes[holeIndex].Length;
                holeIndex++;
                continue;
            }

            var runEnd = holeIndex < holes.Count ? (long)holes[holeIndex].Offset : logicalLength;
            source.Seek(position, SeekOrigin.Begin);
            var run = new BoundedReadStream(source, runEnd - position);

            ISegmentReader segmentReader = _policy.SegmentationProfile == SegmentationProfile.CdcV1
                ? new CdcSegmentReader(run, _policy.CdcParameters!.Value)
                : new FixedSegmentReader(run, _policy.SegmentSize);

            while (await segmentReader.ReadNextAsync(segmentBuffer, cancellationToken).ConfigureAwait(false) is { } segment)
            {
                var plaintext = segmentBuffer.AsMemory(0, segment.Length);
                var contentId = ContentHasher.Hash(plaintext.Span);
                wholeFile.AppendData(plaintext.Span);

                var objectId = await AppendSegmentRecordAsync(contentId, plaintext, cancellationToken).ConfigureAwait(false);
                references.Add(new SegmentReference(position + segment.Offset, segment.Length, objectId));
                contentIds.Add(contentId);
            }

            position = runEnd;
        }
        }
        finally
        {
            ReturnBuffer(segmentBuffer);
        }

        var hash = new byte[32];
        wholeFile.GetHashAndReset(hash);

        return new ArchiveResult(
            references,
            contentIds,
            BlobsSealedSince(sealedBefore),
            logicalLength,
            hash,
            _policy.SegmentationProfile,
            _policy.SegmentSize,
            _policy.CdcParameters);
    }

    /// <summary>
    /// Archives a stream that must fit in exactly one segment — the v1
    /// bound for alternate data streams (ADR-0026 §Decision 5). Returns
    /// null when the content exceeds one segment; the caller records error
    /// reason 6 naming the stream, never a truncated copy.
    /// </summary>
    public async ValueTask<SingleSegmentRecord?> TryArchiveSingleSegmentAsync(
        Stream source, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(source);

        var segmentBuffer = RentBuffer();
        try
        {
            var length = 0;
            int read;
            while (length < _policy.MaximumSegmentBytes &&
                   (read = await source.ReadAsync(
                       segmentBuffer.AsMemory(length, _policy.MaximumSegmentBytes - length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            // Anything left beyond the segment bound means "does not fit".
            var probe = new byte[1];
            if (await source.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0)
            {
                return null;
            }

            var plaintext = segmentBuffer.AsMemory(0, length);
            var contentId = ContentHasher.Hash(plaintext.Span);
            var objectId = await AppendSegmentRecordAsync(contentId, plaintext, cancellationToken).ConfigureAwait(false);
            return new SingleSegmentRecord(objectId, contentId, (ulong)length);
        }
        finally
        {
            ReturnBuffer(segmentBuffer);
        }
    }

    /// <summary>
    /// Seals the open blob, if any, and waits for every queued upload to be
    /// acknowledged (specification 05 §5: job completion seals).
    /// </summary>
    /// <param name="cancellationToken">Cancels the seal.</param>
    /// <returns>A task that completes when every blob is durable.</returns>
    /// <remarks>
    /// This is the drain barrier ADR-0029 §2 requires. Publication step 6 may
    /// not name a blob in an index delta until that blob is durable, so "every
    /// upload acknowledged" has to be a point in time — and this is it.
    /// </remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        await DrainUploadsAsync().ConfigureAwait(false);
    }

    private async ValueTask DrainUploadsAsync()
    {
        _uploads.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_uploadWorkers).ConfigureAwait(false);
        }
        catch
        {
            // Task.WhenAll surfaces one exception; the rest are on the tasks.
            // Rethrowing the first is right here — an upload failure fails the
            // job, and the others are the same story told twice.
            throw;
        }
    }

    private async Task UploadWorkerAsync()
    {
        try
        {
            await foreach (var sealedBlob in _uploads.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await UploadAsync(sealedBlob).ConfigureAwait(false);
            }
        }
        catch
        {
            // A dead worker must not leave the producer parked. Without this the
            // failing worker simply stops reading: once every worker has gone the
            // same way and the bounded channel is full, SealAndQueueAsync blocks
            // in WriteAsync for ever, because nothing left alive will ever
            // complete the channel it is waiting on.
            //
            // Completing without an error is deliberate. Completing *with* one
            // makes every healthy sibling throw ChannelClosedException wrapping
            // it, and then the drain reports whichever wrapper it reaches first
            // instead of the upload that actually broke. The cause stays on this
            // task, which is where DrainUploadsAsync looks for it.
            _uploads.Writer.TryComplete();
            throw;
        }
    }

    private async ValueTask UploadAsync(SealedBlob sealedBlob)
    {
        await using (sealedBlob.ConfigureAwait(false))
        {
            var storeBlobKey = _storeKeyDeriver.Derive(sealedBlob.BlobId);
            var storeKey = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeBlobKey);

            // 08 §3.1: no blob is uploaded before an unretired intent naming
            // it is durable. With several uploads in flight this is enforced
            // per blob rather than per job — which is what ADR-0029 §2 requires
            // and why the intent scope had to become safe for concurrent use.
            if (_intentScope is not null)
            {
                await _intentScope.EnsureCoveredAsync(sealedBlob.BlobId, CancellationToken.None).ConfigureAwait(false);
            }

            var put = await _store.PutAsync(
                storeKey, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None)
                .ConfigureAwait(false);

            if (put.Outcome == PutOutcome.PreconditionFailed)
            {
                throw new IOException(Strings.FormatPreparedSegment_StoreRefusedBlobWithFailed(storeKey));
            }

            // 05 §5.1: the only re-put a writer may treat as success is a
            // byte-identical one. This blob's identifier was freshly
            // allocated, so an existing object under its key means the
            // sequence state regressed and another run's bytes are there;
            // an index delta published over them would commit a snapshot
            // that cannot be restored.
            if (put.Outcome == PutOutcome.AlreadyExists &&
                !await SealedBlobReadback.MatchesAsync(_store, storeKey, sealedBlob, CancellationToken.None).ConfigureAwait(false))
            {
                throw new IOException(Strings.FormatBlobUpload_StoreHeldDifferentBytesUnder(storeKey));
            }

            // ADR-0022 §Decision 7 case 4 is now satisfied — the blob is
            // durable and a durable intent names it — so its counter has its
            // accounting object and owes no void delta to any later run.
            // Without a scope the case never becomes true, and the counter's
            // fate stays with the caller who chose to upload uncovered.
            if (_intentScope is not null)
            {
                _counters.MarkAccounted(sealedBlob.BlobCounter);
            }

            EngineDiagnostics.BlobsSealed.Add(1, new KeyValuePair<string, object?>("class", "data"));
            EngineDiagnostics.BlobFillFraction.Record(
                sealedBlob.Length / (double)_policy.BlobWriteProfile.TargetSizeBytes);

            var archived = new ArchivedBlob(
                sealedBlob.BlobId,
                storeBlobKey,
                storeKey,
                sealedBlob.Digest,
                sealedBlob.RecordTable.Count,
                sealedBlob.Length,
                sealedBlob.RecordTable);

            lock (_blobGate)
            {
                // Order here is incidental (ADR-0029 §1): the index entry list
                // is built from these and validated on content, not sequence.
                _blobs.Add(archived);
            }
        }
    }

    /// <summary>
    /// Appends one segment serially — the sparse and single-segment paths,
    /// which produce at most one segment at a time and so have nothing to
    /// overlap. It runs the same stages as the pipeline, in the same order,
    /// without the staging.
    /// </summary>
    private async ValueTask<ObjectId> AppendSegmentRecordAsync(
        ContentId contentId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var objectId = _objectIdDeriver.Derive(ObjectType.SegmentRecord, contentId);

        // Segment reuse by object identifier (specification 09 §6;
        // NFR-PERF-010): equal content derives an equal identifier, and an
        // identifier the index already locates — or this session already
        // claimed — needs no new record. Keyed on the object id so the test
        // survives a catalogue rebuild.
        if (await MayReuseSegmentAsync(objectId, cancellationToken).ConfigureAwait(false) ||
            !_writtenThisSession.TryAdd(objectId, 0))
        {
            EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "true"));
            return objectId;
        }

        var codec = RentCodec();
        byte[]? compressed = null;
        var payload = plaintext;
        var profile = CompressionProfile.None;

        try
        {
            if (codec is not null)
            {
                var destination = RentBuffer();
                if (codec.TryCompressForStorage(
                        plaintext.Span, _policy.Compression.ThresholdPermille, destination, out var written))
                {
                    compressed = destination;
                    payload = destination.AsMemory(0, written);
                    profile = CompressionProfile.ZstdV1;
                }
                else
                {
                    ReturnBuffer(destination);
                }
            }

            ReturnCodec(codec);
            codec = null;

            if (_writer is not null && !_writer.CanAppend(payload.Length))
            {
                await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
            }

            _writer ??= OpenWriter();

            await _writer.AppendRecordAsync(
                ObjectType.SegmentRecord,
                objectId,
                profile,
                (ulong)plaintext.Length,
                payload,
                cancellationToken).ConfigureAwait(false);

            EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "false"));
            EngineDiagnostics.ArchiveBytesStored.Add(payload.Length);

            if (_writer.ShouldSeal)
            {
                await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
            }

            return objectId;
        }
        finally
        {
            ReturnCodec(codec);
            if (compressed is not null)
            {
                ReturnBuffer(compressed);
            }
        }
    }

    /// <summary>
    /// Opens the blob to append to: the spool an interrupted run left behind
    /// if it can be resumed, otherwise a new blob.
    /// </summary>
    private BlobWriter OpenWriter()
    {
        // Once per session, and only when a blob is actually wanted — a
        // session that archives nothing leaves the spool for the next one
        // rather than discarding work it never looked at.
        if (!_resumeAttempted)
        {
            _resumeAttempted = true;
            var resumed = TryResumeSpool();
            if (resumed is not null)
            {
                return resumed;
            }
        }

        return BlobWriter.Create(
            _repositoryId,
            _writerId,
            _generation,
            BlobClass.Data,
            _classKey,
            _counters.AllocateNext(),
            _policy.EncryptionProfile,
            _policy.BlobWriteProfile,
            _spoolDirectory,
            pinned: _pinned);
    }

    /// <summary>
    /// Resumes the spool an interrupted run left, or <see langword="null"/>
    /// when there is nothing safely resumable (specification 05 §6.3;
    /// FR-ARCH-011, NFR-SEC-003).
    /// </summary>
    private BlobWriter? TryResumeSpool()
    {
        var result = BlobWriter.TryResume(
            _spoolDirectory,
            _repositoryId,
            _writerId,
            _generation,
            BlobClass.Data,
            _classKey,
            _policy.EncryptionProfile,
            _policy.BlobWriteProfile,
            _pinned);

        if (result is not ResumeResult.Resumed resumed)
        {
            // NoSpool, or MustRestart having already deleted the spool and its
            // sidecar. Either way the caller creates a new blob, which draws a
            // fresh salt and so reuses no ordinal (05 §6.2).
            return null;
        }

        var writer = resumed.Writer;

        // The blob arrives holding records this session did not write. Its
        // segments have to enter the dedup set, or the session appends a
        // second copy of a segment the blob already carries — the same
        // content at a second ordinal, which would inflate the blob and
        // publish two records where the manifest references one.
        foreach (var entry in writer.Entries)
        {
            _writtenThisSession.TryAdd(entry.ObjectId, 0);
        }

        // Nothing else needs rebuilding: the index entries for these records
        // are derived from the sealed blob's own record table at seal time,
        // which the resumed writer holds in full, and the blob's counter and
        // salt come from its envelope rather than the allocator.
        return writer;
    }

    private async ValueTask SealAndQueueAsync(CancellationToken cancellationToken)
    {
        var sealedBlob = await _writer!.SealAsync(cancellationToken).ConfigureAwait(false);

        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = null;

        // Hand it over and carry on. If a worker has already failed the channel
        // is complete, and the write fails here rather than losing the blob
        // silently.
        try
        {
            if (!_uploads.Writer.TryWrite(sealedBlob))
            {
                await _uploads.Writer.WriteAsync(sealedBlob, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            await sealedBlob.DisposeAsync().ConfigureAwait(false);

            // The queue is closed because a worker died. Draining reports what
            // actually went wrong; a bare ChannelClosedException would name the
            // plumbing that noticed rather than the upload that failed.
            await DrainUploadsAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool HolesAreWellFormed(IReadOnlyList<SparseExtent> holes, long logicalLength)
    {
        ulong previousEnd = 0;
        foreach (var hole in holes)
        {
            if (hole.Length == 0 || hole.Offset < previousEnd || hole.Offset + hole.Length > (ulong)logicalLength)
            {
                return false;
            }

            previousEnd = hole.Offset + hole.Length;
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Workers first. A producer that threw reaches disposal with workers
        // parked on the channel or mid-put, and everything torn down below —
        // the class key, the derivers — is still in use until they stop, so
        // an upload finishing after disposal returned would be work escaping
        // the lifetime that owned it. Their own failures stay here: the
        // exception that ended the session is already propagating, and a
        // secondary upload failure must not replace it. What they uploaded is
        // intent-covered either way (04 §5.1 row 4).
        _uploads.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_uploadWorkers).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Secondary to the failure that brought the session here.
        }

        if (_writer is not null)
        {
            // A blob still open here means the session did not finish:
            // FlushAsync seals the open blob, so reaching disposal with one
            // is the interrupted case (04 §5.1 row 3, "partial spool, nothing
            // uploaded"). Abandon rather than dispose — the spool and its
            // sidecar stay on disk for the next session to resume (05 §6.3)
            // instead of being deleted along with the work they hold. A
            // partial spool is still never uploaded.
            await _writer.AbandonAsync().ConfigureAwait(false);
            _writer = null;
        }

        CryptographicOperations.ZeroMemory(_classKey);

        // The segment buffers held file plaintext. They are zeroed here, once,
        // rather than on every return — see ReturnBuffer.
        while (_buffers.TryTake(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        _objectIdDeriver.Dispose();
        _storeKeyDeriver.Dispose();

        // Each holds a native compressor context, so they are disposed rather
        // than dropped. The bag is only reachable here once every prepared
        // segment has been drained, which is what returns them to it.
        while (_codecs.TryTake(out var codec))
        {
            codec.Dispose();
        }
    }

    /// <summary>
    /// A read-only view of the next <c>length</c> bytes of an underlying
    /// stream — how one data run of a sparse file is presented to a
    /// segment reader without copying.
    /// </summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = await inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
