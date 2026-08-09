using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Index;

/// <summary>One writer watermark: apply any delta whose sequence exceeds it (specification 07 §5 key 3).</summary>
public sealed record WriterWatermark(WriterId WriterId, ulong HighestSequence);

/// <summary>
/// An index checkpoint (specification 07 §5): a compacted view of the deltas
/// it subsumes, with the complete shard coverage enumerated so a reader can
/// tell "no entries in this shard" from "this shard not covered".
/// </summary>
public sealed record Checkpoint
{
    /// <summary>The generation (key 1).</summary>
    public required ulong Generation { get; init; }

    /// <summary>The exact deltas this checkpoint covers (key 2).</summary>
    public IReadOnlyList<DeltaId> SubsumedDeltaIds { get; init; } = [];

    /// <summary>Per-writer watermarks (key 3).</summary>
    public IReadOnlyList<WriterWatermark> WriterWatermarks { get; init; } = [];

    /// <summary>Every shard covered (key 4), parallel to <see cref="ShardHashesList"/>.</summary>
    public IReadOnlyList<ushort> ShardSet { get; init; } = [];

    /// <summary>SHA-256 per shard (key 5); preimage per ADR-0022 §Decision 6.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ShardHashesList { get; init; } = [];

    /// <summary>The writer's previous checkpoint (key 6); null for its first.</summary>
    public CheckpointId? PredecessorCheckpointId { get; init; }

    /// <summary>The entries (key 7), in §2.1 form.</summary>
    public IReadOnlyList<IndexEntry> Entries { get; init; } = [];

    /// <summary>The publishing writer (key 8).</summary>
    public required WriterId WriterId { get; init; }
}

/// <summary>A decoded checkpoint with its signed bytes and signature.</summary>
public sealed record DecodedCheckpoint(Checkpoint Checkpoint, ReadOnlyMemory<byte> SignedBytes, ReadOnlyMemory<byte> Signature);

/// <summary>
/// Encodes and decodes checkpoints with the two-pass signature over keys
/// 1–8 (specification 07 §5, 06 §6.1 semantics).
/// </summary>
public static class CheckpointCodec
{
    /// <summary>Encodes keys 1–8 — the exact bytes the signature covers.</summary>
    public static byte[] EncodeForSigning(Checkpoint checkpoint)
    {
        ThrowHelper.ThrowIfNull(checkpoint);
        Validate(checkpoint);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, checkpoint, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–8 plus the signature at key 9.</summary>
    public static byte[] Encode(Checkpoint checkpoint, ReadOnlySpan<byte> signature)
    {
        ThrowHelper.ThrowIfNull(checkpoint);
        Validate(checkpoint);

        if (signature.Length != 64)
        {
            throw new IndexFormatException("A checkpoint signature is exactly 64 bytes (specification 07 §5).");
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, checkpoint, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>Decodes a stored checkpoint and rebuilds the signed prefix.</summary>
    /// <exception cref="IndexFormatException">The bytes violate specification 07 §5 — a damage finding.</exception>
    public static DecodedCheckpoint Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new IndexFormatException($"The checkpoint is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static DecodedCheckpoint DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ulong? generation = null;
        List<DeltaId> subsumed = [];
        List<WriterWatermark> watermarks = [];
        List<ushort> shardSet = [];
        List<ReadOnlyMemory<byte>> shardHashes = [];
        CheckpointId? predecessor = null;
        List<IndexEntry> entries = [];
        WriterId? writerId = null;
        byte[]? signature = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    generation = reader.ReadUnsignedInteger();
                    break;
                case 2:
                    var subsumedCount = reader.ReadStartArray(maxCount: 1_000_000);
                    for (var j = 0; j < subsumedCount; j++)
                    {
                        subsumed.Add(DeltaId.FromBytes(reader.ReadFixedByteString(16)));
                    }

                    reader.ReadEndArray();
                    break;
                case 3:
                    var watermarkCount = reader.ReadStartArray(maxCount: 65_536);
                    for (var j = 0; j < watermarkCount; j++)
                    {
                        var pair = reader.ReadStartArray(maxCount: 2);
                        if (pair != 2)
                        {
                            throw new IndexFormatException(
                                "A writer watermark is exactly [writer_id, highest_sequence] (specification 07 §5 key 3).");
                        }

                        watermarks.Add(new WriterWatermark(
                            WriterId.FromBytes(reader.ReadFixedByteString(16)),
                            reader.ReadUnsignedInteger()));
                        reader.ReadEndArray();
                    }

                    reader.ReadEndArray();
                    break;
                case 4:
                    var shardCount = reader.ReadStartArray(maxCount: 65_536);
                    for (var j = 0; j < shardCount; j++)
                    {
                        shardSet.Add(reader.ReadUInt16());
                    }

                    reader.ReadEndArray();
                    break;
                case 5:
                    var hashCount = reader.ReadStartArray(maxCount: 65_536);
                    for (var j = 0; j < hashCount; j++)
                    {
                        shardHashes.Add(reader.ReadFixedByteString(32));
                    }

                    reader.ReadEndArray();
                    break;
                case 6:
                    predecessor = CheckpointId.FromBytes(reader.ReadFixedByteString(16));
                    break;
                case 7:
                    entries = IndexDeltaCodec.ReadEntries(reader);
                    break;
                case 8:
                    writerId = WriterId.FromBytes(reader.ReadFixedByteString(16));
                    break;
                case 9:
                    signature = reader.ReadFixedByteString(64);
                    break;
                default:
                    throw new IndexFormatException(
                        "The checkpoint carries an unknown key; specification 07 §5 assigns keys 1-9 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (generation is null || writerId is null || signature is null)
        {
            throw new IndexFormatException("The checkpoint omits a mandatory key (specification 07 §5).");
        }

        var checkpoint = new Checkpoint
        {
            Generation = generation.Value,
            SubsumedDeltaIds = subsumed,
            WriterWatermarks = watermarks,
            ShardSet = shardSet,
            ShardHashesList = shardHashes,
            PredecessorCheckpointId = predecessor,
            Entries = entries,
            WriterId = writerId.Value,
        };

        Validate(checkpoint);

        return new DecodedCheckpoint(checkpoint, EncodeForSigning(checkpoint), signature);
    }

    private static void Validate(Checkpoint checkpoint)
    {
        if (checkpoint.ShardSet.Count != checkpoint.ShardHashesList.Count)
        {
            throw new IndexFormatException(
                "shard_set and shard_hashes are positionally parallel and must agree in length (specification 07 §5).");
        }

        // A checkpoint MUST enumerate every shard it covers (07 §8): every
        // entry's shard appears in shard_set.
        var covered = new HashSet<ushort>(checkpoint.ShardSet);
        foreach (var entry in checkpoint.Entries)
        {
            if (!covered.Contains(entry.Shard))
            {
                throw new IndexFormatException(
                    $"The checkpoint carries an entry in shard {entry.Shard} but does not enumerate it in shard_set (specification 07 §8).");
            }
        }
    }

    private static void WriteBody(CanonicalCborWriter writer, Checkpoint checkpoint, byte[]? signature)
    {
        var keyCount = 7
            + (checkpoint.PredecessorCheckpointId is not null ? 1 : 0)
            + (signature is not null ? 1 : 0);

        writer.WriteStartMap(keyCount);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(checkpoint.Generation);
        writer.WriteKey(2);
        writer.WriteStartArray(checkpoint.SubsumedDeltaIds.Count);
        foreach (var deltaId in checkpoint.SubsumedDeltaIds)
        {
            writer.WriteByteString(deltaId.ToArray());
        }

        writer.WriteEndArray();
        writer.WriteKey(3);
        writer.WriteStartArray(checkpoint.WriterWatermarks.Count);
        foreach (var watermark in checkpoint.WriterWatermarks)
        {
            writer.WriteStartArray(2);
            writer.WriteByteString(watermark.WriterId.ToArray());
            writer.WriteUnsignedInteger(watermark.HighestSequence);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteKey(4);
        writer.WriteStartArray(checkpoint.ShardSet.Count);
        foreach (var shard in checkpoint.ShardSet)
        {
            writer.WriteUnsignedInteger(shard);
        }

        writer.WriteEndArray();
        writer.WriteKey(5);
        writer.WriteStartArray(checkpoint.ShardHashesList.Count);
        foreach (var hash in checkpoint.ShardHashesList)
        {
            writer.WriteByteString(hash.Span);
        }

        writer.WriteEndArray();

        if (checkpoint.PredecessorCheckpointId is { } predecessor)
        {
            writer.WriteKey(6);
            writer.WriteByteString(predecessor.ToArray());
        }

        writer.WriteKey(7);
        IndexDeltaCodec.WriteEntries(writer, checkpoint.Entries);
        writer.WriteKey(8);
        writer.WriteByteString(checkpoint.WriterId.ToArray());

        if (signature is not null)
        {
            writer.WriteKey(9);
            writer.WriteByteString(signature);
        }

        writer.WriteEndMap();
    }
}
