using System.Buffers;
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
    private readonly byte[] _segmentBuffer;
    private readonly byte[] _compressed;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly StoreBlobKeyDeriver _storeKeyDeriver;
    private readonly ZstdSegmentCodec? _codec;
    private readonly List<ArchivedBlob> _blobs = [];

    // Upload leaves the archive loop (ADR-0029 §2). A sealed blob is handed to
    // a bounded set of workers and the loop continues; before this, the whole
    // pipeline stalled for the duration of every PUT — the largest structural
    // stall in the design, and worst precisely where it matters most, on a slow
    // or remote destination.
    private readonly Channel<SealedBlob> _uploads;
    private readonly Task[] _uploadWorkers;
    private readonly Lock _blobGate = new();
    private readonly Func<ObjectId, bool>? _segmentExists;
    private readonly HashSet<ObjectId> _writtenThisSession = [];
    private BlobWriter? _writer;

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
        Func<ObjectId, bool>? segmentExists)
    {
        _segmentExists = segmentExists;
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
        _segmentBuffer = ArrayPool<byte>.Shared.Rent(policy.MaximumSegmentBytes);
        _compressed = ArrayPool<byte>.Shared.Rent(policy.MaximumSegmentBytes);
        _objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        _storeKeyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        _codec = policy.Compression.Profile == CompressionProfile.ZstdV1
            ? new ZstdSegmentCodec(policy.Compression.ZstdLevel, policy.MaximumSegmentBytes)
            : null;

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
    public IReadOnlyList<ArchivedBlob> Blobs => _blobs;

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
        var sealedBefore = _blobs.Count;
        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        ISegmentReader segmentReader = usesCdc
            ? new CdcSegmentReader(source, _policy.CdcParameters!.Value)
            : new FixedSegmentReader(source, _policy.SegmentSize);

        var logicalLength = 0L;

        while (await segmentReader.ReadNextAsync(_segmentBuffer, cancellationToken).ConfigureAwait(false) is { } segment)
        {
            var plaintext = _segmentBuffer.AsMemory(0, segment.Length);
            logicalLength = segment.Offset + segment.Length;
            EngineDiagnostics.ArchiveBytesLogical.Add(segment.Length);

            // 04 §5 order: content id, object id, compress, ordinal, encrypt.
            var contentId = ContentHasher.Hash(plaintext.Span);
            wholeFile.AppendData(plaintext.Span);

            // Content-based reuse (09 §6, cdc-v1): the same bytes anywhere
            // in the prior version need no new record.
            if (priorByContent is not null &&
                priorByContent.TryGetValue(contentId, out var prior) &&
                prior.Length == segment.Length)
            {
                EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "true"));
                references.Add(new SegmentReference(segment.Offset, segment.Length, prior.ObjectId));
                contentIds.Add(contentId);
                continue;
            }

            // Positional reuse (09 §6, fixed-v1): identical content at the
            // same position of the prior version needs no new record.
            if (comparable && !usesCdc &&
                segment.Index < priorVersion!.SegmentReferences.Count &&
                priorVersion.SegmentReferences[(int)segment.Index].LogicalLength == segment.Length &&
                priorVersion.SegmentContentIds[(int)segment.Index] == contentId)
            {
                EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "true"));
                references.Add(new SegmentReference(
                    segment.Offset,
                    segment.Length,
                    priorVersion.SegmentReferences[(int)segment.Index].ObjectId));
                contentIds.Add(contentId);
                continue;
            }

            var objectId = await AppendSegmentRecordAsync(contentId, plaintext, cancellationToken).ConfigureAwait(false);
            references.Add(new SegmentReference(segment.Offset, segment.Length, objectId));
            contentIds.Add(contentId);
        }

        var hash = new byte[32];
        wholeFile.GetHashAndReset(hash);

        return new ArchiveResult(
            references,
            contentIds,
            _blobs.Skip(sealedBefore).ToList(),
            logicalLength,
            hash,
            _policy.SegmentationProfile,
            _policy.SegmentSize,
            _policy.CdcParameters);
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
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(holes);

        if (!HolesAreWellFormed(holes, logicalLength))
        {
            var dense = await ArchiveFileAsync(source, priorVersion: null, cancellationToken).ConfigureAwait(false);
            return dense;
        }

        var references = new List<SegmentReference>();
        var contentIds = new List<ContentId>();
        var sealedBefore = _blobs.Count;
        using var wholeFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long position = 0;
        var holeIndex = 0;

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

            while (await segmentReader.ReadNextAsync(_segmentBuffer, cancellationToken).ConfigureAwait(false) is { } segment)
            {
                var plaintext = _segmentBuffer.AsMemory(0, segment.Length);
                var contentId = ContentHasher.Hash(plaintext.Span);
                wholeFile.AppendData(plaintext.Span);

                var objectId = await AppendSegmentRecordAsync(contentId, plaintext, cancellationToken).ConfigureAwait(false);
                references.Add(new SegmentReference(position + segment.Offset, segment.Length, objectId));
                contentIds.Add(contentId);
            }

            position = runEnd;
        }

        var hash = new byte[32];
        wholeFile.GetHashAndReset(hash);

        return new ArchiveResult(
            references,
            contentIds,
            _blobs.Skip(sealedBefore).ToList(),
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
        ArgumentNullException.ThrowIfNull(source);

        var length = 0;
        int read;
        while (length < _policy.MaximumSegmentBytes &&
               (read = await source.ReadAsync(
                   _segmentBuffer.AsMemory(length, _policy.MaximumSegmentBytes - length), cancellationToken)
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

        var plaintext = _segmentBuffer.AsMemory(0, length);
        var contentId = ContentHasher.Hash(plaintext.Span);
        var objectId = await AppendSegmentRecordAsync(contentId, plaintext, cancellationToken).ConfigureAwait(false);
        return new SingleSegmentRecord(objectId, contentId, (ulong)length);
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
        await foreach (var sealedBlob in _uploads.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await UploadAsync(sealedBlob).ConfigureAwait(false);
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
                throw new IOException($"The store refused blob '{storeKey}' with a failed precondition.");
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

    private async ValueTask<ObjectId> AppendSegmentRecordAsync(
        ContentId contentId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var objectId = _objectIdDeriver.Derive(ObjectType.SegmentRecord, contentId);

        // Segment reuse by object identifier (specification 09 §6;
        // NFR-PERF-010): equal content derives an equal identifier, and an
        // identifier the index already locates — or this session already
        // wrote — needs no new record. Keyed on the object id so the test
        // survives a catalogue rebuild.
        if (_writtenThisSession.Contains(objectId) || _segmentExists?.Invoke(objectId) == true)
        {
            EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "true"));
            return objectId;
        }

        var payload = plaintext;
        var profile = CompressionProfile.None;
        if (_codec is not null &&
            _codec.TryCompressForStorage(plaintext.Span, _policy.Compression.ThresholdPermille, _compressed, out var written))
        {
            payload = _compressed.AsMemory(0, written);
            profile = CompressionProfile.ZstdV1;
        }

        if (_writer is not null && !_writer.CanAppend(payload.Length))
        {
            await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        _writer ??= BlobWriter.Create(
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

        await _writer.AppendRecordAsync(
            ObjectType.SegmentRecord,
            objectId,
            profile,
            (ulong)plaintext.Length,
            payload,
            cancellationToken).ConfigureAwait(false);

        _writtenThisSession.Add(objectId);
        EngineDiagnostics.ArchiveSegments.Add(1, new KeyValuePair<string, object?>("reused", "false"));
        EngineDiagnostics.ArchiveBytesStored.Add(payload.Length);

        if (_writer.ShouldSeal)
        {
            await SealAndQueueAsync(cancellationToken).ConfigureAwait(false);
        }

        return objectId;
    }

    private async ValueTask SealAndQueueAsync(CancellationToken cancellationToken)
    {
        var sealedBlob = await _writer!.SealAsync(cancellationToken).ConfigureAwait(false);

        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = null;

        // Hand it over and carry on. If a worker has already failed the channel
        // is complete, and the write fails here rather than losing the blob
        // silently.
        if (!_uploads.Writer.TryWrite(sealedBlob))
        {
            await _uploads.Writer.WriteAsync(sealedBlob, cancellationToken).ConfigureAwait(false);
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
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        CryptographicOperations.ZeroMemory(_classKey);
        ArrayPool<byte>.Shared.Return(_segmentBuffer, clearArray: true);
        ArrayPool<byte>.Shared.Return(_compressed, clearArray: true);
        _objectIdDeriver.Dispose();
        _storeKeyDeriver.Dispose();
        _codec?.Dispose();
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
