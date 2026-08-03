using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Index;

/// <summary>
/// Publishes index deltas, void deltas, and checkpoints (specification
/// 07 §2, §4, §5; FR-MAN-008): each object is signed over its 1–8 prefix,
/// sealed under the FBPKSREC framing with its sequence number as the record
/// counter, and put at its <c>/index/…</c> key with a freshly allocated
/// random identifier (ADR-0022 §Decision 2). Retry re-puts the identical
/// sealed buffer — the identifier and bytes are held until acknowledged.
/// </summary>
public sealed class IndexPublisher : IDisposable
{
    private readonly IObjectStore _store;
    private readonly RepositoryId _repositoryId;
    private readonly WriterId _writerId;
    private readonly KeyHierarchy _hierarchy;
    private readonly WriterSequence _sequence;
    private readonly ObjectIdDeriver _objectIdDeriver;

    /// <summary>Creates a publisher for one writer.</summary>
    public IndexPublisher(
        IObjectStore store,
        RepositoryId repositoryId,
        WriterId writerId,
        KeyHierarchy hierarchy,
        WriterSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(sequence);

        _store = store;
        _repositoryId = repositoryId;
        _writerId = writerId;
        _hierarchy = hierarchy;
        _sequence = sequence;
        _objectIdDeriver = new ObjectIdDeriver(hierarchy.DeriveContentIdKey());
    }

    /// <summary>
    /// Publishes a delta: allocates the next sequence, signs keys 1–8, seals,
    /// puts, and only then marks the sequence accounted.
    /// </summary>
    public async ValueTask<DeltaId> PublishDeltaAsync(
        ulong generation,
        IReadOnlyList<BlobId> coveredBlobIds,
        IReadOnlyList<IndexEntry> entries,
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
            Entries = entries,
        };

        var deltaId = await PublishDeltaObjectAsync(delta, cancellationToken).ConfigureAwait(false);
        _sequence.MarkAccounted(sequence, deltaId);
        return deltaId;
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
        };

        var deltaId = await PublishDeltaObjectAsync(delta, cancellationToken).ConfigureAwait(false);
        _sequence.MarkAccounted(sequence, deltaId);
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
        using var signer = RepositorySigner.Create(_hierarchy, ToKeyGeneration(generation));
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
        var keyGeneration = ToKeyGeneration(generation);
        var objectId = _objectIdDeriver.Derive(objectType, ContentHasher.Hash(encoded));
        var metadataKey = _hierarchy.DeriveMetadataKey(keyGeneration);

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
            throw new IOException($"The store refused index object '{key}' with a failed precondition.");
        }
    }

    private static KeyGeneration ToKeyGeneration(ulong generation)
    {
        if (generation > uint.MaxValue)
        {
            throw new IndexFormatException(
                $"Generation {generation} exceeds the key hierarchy's u32 generation space (specification 03 §4).");
        }

        return new KeyGeneration((uint)generation);
    }

    /// <inheritdoc />
    public void Dispose() => _objectIdDeriver.Dispose();
}
