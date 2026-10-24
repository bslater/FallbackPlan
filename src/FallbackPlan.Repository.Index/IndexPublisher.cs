using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Repository.Index.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>One delta a checkpoint carries: its identifier and its stored object, exactly as at its key.</summary>
/// <param name="DeltaId">The delta's identifier (07 §5 key 2).</param>
/// <param name="Stored">The sealed bytes at the delta's key (07 §5 key 10).</param>
public sealed record BundledDelta(DeltaId DeltaId, ReadOnlyMemory<byte> Stored);

/// <summary>
/// Publishes index deltas, void deltas, and checkpoints (specification
/// 07 §2, §4, §5; FR-MAN-008): each object is signed over every key but its
/// signature, sealed under the FBPKSREC framing with its sequence number as
/// the record counter, and put at its <c>/index/…</c> key with a freshly
/// allocated random identifier (ADR-0022 §Decision 2). Retry re-puts the
/// identical sealed buffer — the identifier and bytes are held until
/// acknowledged.
/// </summary>
public sealed class IndexPublisher : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly RepositoryWriteCredential _credential;
    private readonly WriterSequence _sequence;
    private readonly ObjectIdDeriver _objectIdDeriver;
    private readonly ILogger _log;
    private readonly ulong? _highestIndexGenerationSeen;

    /// <summary>Creates a publisher for one writer.</summary>
    /// <param name="store">Where the index objects go.</param>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="writerId">The writer.</param>
    /// <param name="credential">What signs and seals.</param>
    /// <param name="sequence">The writer's sequence space.</param>
    /// <param name="logger">Where publication is logged.</param>
    /// <param name="highestIndexGenerationSeen">
    /// The highest index generation the publication has seen in the
    /// repository, zero for none, which makes every delta this publisher
    /// writes carry one (07 §2.4; ADR-0096). Null for a writer below
    /// repository format 4, whose deltas carry none: an older reader is
    /// entitled to read them and would refuse key 12 outright.
    /// </param>
    public IndexPublisher(
        IObjectStore store,
        RepositoryId repositoryId,
        WriterId writerId,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        ILogger? logger = null,
        ulong? highestIndexGenerationSeen = null)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(credential);
        ThrowHelper.ThrowIfNull(sequence);

        _log = logger ?? NullLogger.Instance;
        _store = store;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _credential = credential;
        _sequence = sequence;
        _highestIndexGenerationSeen = highestIndexGenerationSeen;
        _objectIdDeriver = new ObjectIdDeriver(credential.ContentIdKey.ToArray());
    }

    /// <summary>
    /// Publishes a delta: allocates the next sequence, signs, seals, puts, and
    /// only then marks the sequence accounted.
    /// </summary>
    public async ValueTask<DeltaId> PublishDeltaAsync(
        ulong generation,
        IReadOnlyList<BlobId> coveredBlobIds,
        IReadOnlyList<IndexEntry> entries,
        CancellationToken cancellationToken)
    {
        var (deltaId, _) = await PublishDeltaDetailedAsync(
            generation, coveredBlobIds, entries, coveredBlobDigests: [], coveredBlobMerkleRoots: [],
            cancellationToken).ConfigureAwait(false);
        return deltaId;
    }

    /// <summary>
    /// As <see cref="PublishDeltaAsync"/>, also returning the published
    /// delta itself — what a live catalogue applies without re-reading the
    /// store (architecture 02 §7).
    /// </summary>
    /// <param name="generation">The publication generation.</param>
    /// <param name="coveredBlobIds">The blobs this delta's entries live in.</param>
    /// <param name="entries">The entries.</param>
    /// <param name="coveredBlobDigests">
    /// SHA-256 of each covered blob's sealed bytes, parallel to
    /// <paramref name="coveredBlobIds"/>, or empty to publish none
    /// (specification 07 §2.2). This is what makes a blob's digest checkable
    /// by a participant other than the device that sealed it.
    /// </param>
    /// <param name="coveredBlobMerkleRoots">
    /// The Merkle commitment over each covered blob's sealed bytes, parallel
    /// to <paramref name="coveredBlobIds"/>, or empty to publish none
    /// (specification 07 §2.3). Only a writer at repository format 3 or
    /// above may pass these: below it, an older reader is entitled to read
    /// the delta and would refuse key 11 outright.
    /// </param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    public async ValueTask<(DeltaId DeltaId, IndexDelta Delta)> PublishDeltaDetailedAsync(
        ulong generation,
        IReadOnlyList<BlobId> coveredBlobIds,
        IReadOnlyList<IndexEntry> entries,
        IReadOnlyList<ReadOnlyMemory<byte>> coveredBlobDigests,
        IReadOnlyList<ReadOnlyMemory<byte>> coveredBlobMerkleRoots,
        CancellationToken cancellationToken)
    {
        var sequence = _sequence.AllocateNext();

        var delta = new IndexDelta
        {
            WriterId = _writerId,
            Sequence = sequence,
            PredecessorDeltaId = _sequence.LastDeltaId,
            Generation = generation,
            CoveredBlobIds = coveredBlobIds,
            CoveredBlobDigests = coveredBlobDigests,
            CoveredBlobMerkleRoots = coveredBlobMerkleRoots,
            Entries = entries,
            IndexGeneration = NextIndexGeneration(),
        };

        var deltaId = await PublishDeltaObjectAsync(delta, cancellationToken).ConfigureAwait(false);
        _sequence.MarkAccounted(sequence, deltaId);
        Log.DeltaPublished(_log, deltaId, checked((uint)generation), entries.Count);
        return (deltaId, delta);
    }

    /// <summary>
    /// Publishes a void delta at a specific skipped sequence — the 07 §4
    /// obligation for a number allocated and never used.
    /// </summary>
    public async ValueTask<DeltaId> PublishVoidDeltaAsync(
        ulong generation,
        ulong sequence,
        CancellationToken cancellationToken)
    {
        var delta = new IndexDelta
        {
            WriterId = _writerId,
            Sequence = sequence,
            PredecessorDeltaId = _sequence.LastDeltaId,
            Generation = generation,
            IsVoid = true,
            IndexGeneration = NextIndexGeneration(),
        };

        var deltaId = await PublishDeltaObjectAsync(delta, cancellationToken).ConfigureAwait(false);
        _sequence.MarkAccounted(sequence, deltaId);
        Log.VoidDeltaPublished(_log, sequence);
        return deltaId;
    }

    /// <summary>
    /// Publishes a checkpoint: shard set and hashes computed from the
    /// entries (ADR-0022 §Decision 6), signed, sealed, put. The record
    /// counter comes from the shared sequence space; a checkpoint accounts
    /// for its own counter through its cleartext FBPKSREC prefix, which
    /// readers can enumerate.
    /// </summary>
    public async ValueTask<CheckpointId> PublishCheckpointAsync(
        ulong generation,
        IReadOnlyList<DeltaId> subsumedDeltaIds,
        IReadOnlyList<WriterWatermark> writerWatermarks,
        IReadOnlyList<IndexEntry> entries,
        CheckpointId? predecessor,
        CancellationToken cancellationToken)
    {
        var sequence = _sequence.AllocateNext();
        var (shardSet, hashes) = ShardHashes.Compute(entries);

        var checkpoint = new Checkpoint
        {
            Generation = generation,
            SubsumedDeltaIds = subsumedDeltaIds,
            WriterWatermarks = writerWatermarks,
            ShardSet = shardSet,
            ShardHashesList = [.. hashes.Select(hash => (ReadOnlyMemory<byte>)hash)],
            PredecessorCheckpointId = predecessor,
            Entries = entries,
            WriterId = _writerId,
        };

        var stored = SignAndEncode(
            CheckpointCodec.EncodeForSigning(checkpoint),
            bytes => CheckpointCodec.Encode(checkpoint, bytes),
            generation);

        var checkpointId = CheckpointId.NewRandom();
        await SealAndPutAsync(
            stored,
            ObjectType.IndexCheckpoint,
            sequence,
            generation,
            MetadataStoreKeys.IndexCheckpoint(generation, checkpointId),
            cancellationToken).ConfigureAwait(false);

        _sequence.MarkAccounted(sequence);
        return checkpointId;
    }

    /// <summary>
    /// Publishes a format-5 checkpoint carrying <paramref name="deltas"/>
    /// verbatim (07 §5 key 10;
    /// [ADR-0097](../../docs/adr/0097-format-5-a-checkpoint-carries-its-deltas.md)):
    /// each delta's stored object beside its identifier, and nothing of the
    /// checkpoint's own to rank. A reader opens each copy exactly as it would
    /// the object at the delta's key, so this changes how many objects a load
    /// reads and nothing it reaches.
    /// </summary>
    /// <param name="generation">The generation the checkpoint is keyed and signed at.</param>
    /// <param name="deltas">The deltas, each as its stored bytes; at least one.</param>
    /// <param name="predecessor">This writer's previous checkpoint, or null.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    public async ValueTask<CheckpointId> PublishBundledCheckpointAsync(
        ulong generation,
        IReadOnlyList<BundledDelta> deltas,
        CheckpointId? predecessor,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(deltas);
        ArgumentOutOfRangeException.ThrowIfZero(deltas.Count);

        var sequence = _sequence.AllocateNext();
        var checkpoint = new Checkpoint
        {
            Generation = generation,
            SubsumedDeltaIds = [.. deltas.Select(delta => delta.DeltaId)],
            SubsumedDeltas = [.. deltas.Select(delta => delta.Stored)],
            PredecessorCheckpointId = predecessor,
            WriterId = _writerId,
        };

        var stored = SignAndEncode(
            CheckpointCodec.EncodeForSigning(checkpoint),
            bytes => CheckpointCodec.Encode(checkpoint, bytes),
            generation);

        var checkpointId = CheckpointId.NewRandom();
        await SealAndPutAsync(
            stored,
            ObjectType.IndexCheckpoint,
            sequence,
            generation,
            MetadataStoreKeys.IndexCheckpoint(generation, checkpointId),
            cancellationToken).ConfigureAwait(false);

        _sequence.MarkAccounted(sequence);
        Log.CheckpointPublished(_log, checkpointId, deltas.Count);
        return checkpointId;
    }

    // A void delta draws one too: every delta a format-4 writer publishes
    // carries key 12 (07 §2.4), so a reader can age a gap from whichever
    // delta follows it.
    private ulong? NextIndexGeneration() =>
        _highestIndexGenerationSeen is { } highestSeen ? _sequence.AllocateIndexGeneration(highestSeen) : null;

    private async ValueTask<DeltaId> PublishDeltaObjectAsync(IndexDelta delta, CancellationToken cancellationToken)
    {
        var stored = SignAndEncode(
            IndexDeltaCodec.EncodeForSigning(delta),
            bytes => IndexDeltaCodec.Encode(delta, bytes),
            delta.Generation);

        var deltaId = DeltaId.NewRandom();
        await SealAndPutAsync(
            stored,
            ObjectType.IndexDelta,
            delta.Sequence,
            delta.Generation,
            MetadataStoreKeys.IndexDelta(delta.Generation, deltaId),
            cancellationToken).ConfigureAwait(false);

        return deltaId;
    }

    private byte[] SignAndEncode(byte[] signedBytes, Func<byte[], byte[]> encodeWithSignature, ulong generation)
    {
        using var signer = RepositorySigner.Create(_credential, ToKeyGeneration(generation));
        return encodeWithSignature(signer.Sign(signedBytes));
    }

    private async ValueTask SealAndPutAsync(
        byte[] encoded,
        ObjectType objectType,
        ulong counter,
        ulong generation,
        ObjectKey key,
        CancellationToken cancellationToken)
    {
        // The framing refuses a stored length over the limit at read, so an
        // object past it would be put, look published, and fail the first
        // load that needed it. The number already drawn stays outstanding and
        // is voided as any failed publication's is.
        if (encoded.Length > FormatLimits.MaxMetadataObjectSize)
        {
            throw new IndexFormatException(Strings.FormatIndexPublisher_IndexObjectOverMetadataLimit(key, encoded.Length));
        }

        var keyGeneration = ToKeyGeneration(generation);
        var objectId = _objectIdDeriver.Derive(objectType, ContentHasher.Hash(encoded));
        var metadataKey = _credential.DeriveMetadataKey(keyGeneration);

        byte[] sealedObject;
        try
        {
            sealedObject = StandaloneRecordCipher.Seal(
                _repositoryId, metadataKey, keyGeneration, _writerId, counter, objectType, objectId, encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }

        var put = await _store.PutAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedObject, writable: false)),
            PutConditions.IfNotExists,
            cancellationToken).ConfigureAwait(false);

        if (put.Outcome == PutOutcome.PreconditionFailed)
        {
            throw new IOException(Strings.FormatIndexPublisher_StoreRefusedIndexObjectWith(key));
        }
    }

    private static KeyGeneration ToKeyGeneration(ulong generation)
    {
        if (generation > uint.MaxValue)
        {
            throw new IndexFormatException(Strings.FormatIndexPublisher_GenerationExceedsKeyHierarchyS(generation));
        }

        return new KeyGeneration((uint)generation);
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}
