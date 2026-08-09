using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Index;

/// <summary>
/// An index delta (specification 07 §2): one writer's incremental additions
/// to the index, gapless per writer, signed keys 1–8. A void delta
/// (<c>is_void</c>) fills a skipped sequence with nothing (07 §4).
/// </summary>
public sealed record IndexDelta
{
    /// <summary>The writing participant (key 1).</summary>
    public required WriterId WriterId { get; init; }

    /// <summary>The writer's sequence number — strictly increasing, no gaps (key 2).</summary>
    public required ulong Sequence { get; init; }

    /// <summary>The writer's previous delta (key 3); null for its first.</summary>
    public DeltaId? PredecessorDeltaId { get; init; }

    /// <summary>The publication generation (key 4).</summary>
    public required ulong Generation { get; init; }

    /// <summary>
    /// The single shard, when the delta genuinely covers one (key 5) —
    /// optional per ADR-0022 §Decision 4; absent means the shards implied by
    /// the entries. The phase-0 writer always omits it.
    /// </summary>
    public ushort? Shard { get; init; }

    /// <summary>The blobs whose records this delta covers (key 6).</summary>
    public IReadOnlyList<BlobId> CoveredBlobIds { get; init; } = [];

    /// <summary>The entries (key 7); empty for a void delta.</summary>
    public IReadOnlyList<IndexEntry> Entries { get; init; } = [];

    /// <summary>Present and true only for a void delta (key 8; 07 §4).</summary>
    public bool IsVoid { get; init; }

    /// <summary>
    /// SHA-256 of each covered blob's sealed bytes, parallel to
    /// <see cref="CoveredBlobIds"/> (key 10; 07 §2.2). Empty when the writer
    /// published none.
    /// </summary>
    /// <remarks>
    /// The durable home of the blob digest, and the reason it is here rather
    /// than only in the catalogue: a replication receipt has to be checkable
    /// by the participant receiving the blob, and a device-local record is
    /// not. Inside the signature, so what it asserts is what the writer
    /// signed.
    /// </remarks>
    public IReadOnlyList<ReadOnlyMemory<byte>> CoveredBlobDigests { get; init; } = [];
}

/// <summary>A decoded delta: the value, the exact signed bytes, and the signature to verify against the derived signing key for <see cref="IndexDelta.Generation"/>.</summary>
public sealed record DecodedDelta(IndexDelta Delta, ReadOnlyMemory<byte> SignedBytes, ReadOnlyMemory<byte> Signature);

/// <summary>
/// Encodes and decodes index deltas with the 06 §6.1 two-pass signature
/// construction over keys 1–8; the stored form re-encodes with key 9 added.
/// </summary>
public static class IndexDeltaCodec
{
    /// <summary>Encodes keys 1–8 — the exact bytes the signature covers.</summary>
    public static byte[] EncodeForSigning(IndexDelta delta)
    {
        ThrowHelper.ThrowIfNull(delta);
        Validate(delta);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, delta, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–8 plus the signature at key 9.</summary>
    public static byte[] Encode(IndexDelta delta, ReadOnlySpan<byte> signature)
    {
        ThrowHelper.ThrowIfNull(delta);
        Validate(delta);

        if (signature.Length != 64)
        {
            throw new IndexFormatException("A delta signature is exactly 64 bytes (specification 07 §2).");
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, delta, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>Decodes a stored delta and rebuilds the signed prefix.</summary>
    /// <exception cref="IndexFormatException">The bytes violate specification 07 §2 — a damage finding.</exception>
    public static DecodedDelta Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new IndexFormatException($"The delta is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static DecodedDelta DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        WriterId? writerId = null;
        ulong? sequence = null, generation = null;
        DeltaId? predecessor = null;
        ushort? shard = null;
        List<BlobId> covered = [];
        List<ReadOnlyMemory<byte>> digests = [];
        List<IndexEntry> entries = [];
        bool? isVoid = null;
        byte[]? signature = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    writerId = WriterId.FromBytes(reader.ReadFixedByteString(16));
                    break;
                case 2:
                    sequence = reader.ReadUnsignedInteger();
                    break;
                case 3:
                    predecessor = DeltaId.FromBytes(reader.ReadFixedByteString(16));
                    break;
                case 4:
                    generation = reader.ReadUnsignedInteger();
                    break;
                case 5:
                    shard = reader.ReadUInt16();
                    break;
                case 6:
                    var blobCount = reader.ReadStartArray(maxCount: 1_000_000);
                    for (var j = 0; j < blobCount; j++)
                    {
                        covered.Add(BlobId.FromBytes(reader.ReadFixedByteString(16)));
                    }

                    reader.ReadEndArray();
                    break;
                case 7:
                    entries = ReadEntries(reader);
                    break;
                case 8:
                    isVoid = reader.ReadBoolean();
                    break;
                case 9:
                    signature = reader.ReadFixedByteString(64);
                    break;
                case 10:
                    var digestCount = reader.ReadStartArray(maxCount: 65_536);
                    digests = new List<ReadOnlyMemory<byte>>(digestCount);
                    for (var digestIndex = 0; digestIndex < digestCount; digestIndex++)
                    {
                        digests.Add(reader.ReadFixedByteString(32));
                    }

                    reader.ReadEndArray();
                    break;
                default:
                    throw new IndexFormatException(
                        "The delta carries an unknown key; specification 07 §2 assigns keys 1-10 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (writerId is null || sequence is null || generation is null || signature is null)
        {
            throw new IndexFormatException("The delta omits a mandatory key (specification 07 §2).");
        }

        if (isVoid == false)
        {
            throw new IndexFormatException(
                "is_void is present and true only for a void delta (specification 07 §2 key 8).");
        }

        var delta = new IndexDelta
        {
            WriterId = writerId.Value,
            Sequence = sequence.Value,
            PredecessorDeltaId = predecessor,
            Generation = generation.Value,
            Shard = shard,
            CoveredBlobIds = covered,
            Entries = entries,
            IsVoid = isVoid == true,
            CoveredBlobDigests = digests,
        };

        Validate(delta);

        return new DecodedDelta(delta, EncodeForSigning(delta), signature);
    }

    private static void Validate(IndexDelta delta)
    {
        if (delta.IsVoid && (delta.Entries.Count > 0 || delta.CoveredBlobIds.Count > 0))
        {
            throw new IndexFormatException(
                "A void delta is well-formed and signed with empty entries and empty covered_blob_ids (specification 07 §4).");
        }

        // Parallel arrays or no array at all. Pairing what can be paired
        // would silently attach one blob's digest to another blob, which is
        // worse than carrying no digest (specification 07 §2.2).
        if (delta.CoveredBlobDigests.Count > 0 && delta.CoveredBlobDigests.Count != delta.CoveredBlobIds.Count)
        {
            throw new IndexFormatException(
                $"covered_blob_digests has {delta.CoveredBlobDigests.Count} elements against " +
                $"{delta.CoveredBlobIds.Count} covered blobs; the arrays are parallel or absent (specification 07 §2.2).");
        }

        foreach (var digest in delta.CoveredBlobDigests)
        {
            if (digest.Length != 32)
            {
                throw new IndexFormatException(
                    "A covered blob digest is a 32-byte SHA-256 (specification 07 §2.2).");
            }
        }

        if (delta.Shard is { } shard)
        {
            // Present, key 5 asserts a single-shard delta; a mismatched entry
            // is a damage finding (ADR-0022 §Decision 4).
            foreach (var entry in delta.Entries)
            {
                if (entry.Shard != shard)
                {
                    throw new IndexFormatException(
                        $"The delta declares shard {shard} but carries an entry in shard {entry.Shard} (ADR-0022 §Decision 4).");
                }
            }
        }
    }

    internal static void WriteEntries(CanonicalCborWriter writer, IReadOnlyList<IndexEntry> entries)
    {
        writer.WriteStartArray(entries.Count);

        foreach (var entry in entries)
        {
            writer.WriteStartArray(6);
            writer.WriteByteString(entry.ObjectId.ToArray());
            writer.WriteByteString(entry.BlobId.ToArray());
            writer.WriteUnsignedInteger(entry.PhysicalOffset);
            writer.WriteUnsignedInteger(entry.StoredLength);
            writer.WriteUnsignedInteger(entry.PackedProfiles);
            writer.WriteUnsignedInteger((byte)entry.EntryType);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }

    internal static List<IndexEntry> ReadEntries(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(maxCount: 10_000_000);
        var entries = new List<IndexEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var elements = reader.ReadStartArray(maxCount: 8);
            if (elements != 6)
            {
                throw new IndexFormatException(
                    $"An index entry is exactly six elements; got {elements} (specification 07 §2.1).");
            }

            var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(32));
            var blobId = BlobId.FromBytes(reader.ReadFixedByteString(16));
            var physicalOffset = reader.ReadUnsignedInteger();
            var storedLength = reader.ReadUInt32();
            var packed = reader.ReadUInt32();
            var entryType = reader.ReadByte();
            reader.ReadEndArray();

            if (entryType is < 1 or > 2)
            {
                throw new IndexFormatException($"entry_type {entryType} is unassigned (specification 07 §2.1).");
            }

            var (compression, encryption) = IndexEntry.UnpackProfiles(packed);
            entries.Add(new IndexEntry(
                objectId, blobId, physicalOffset, storedLength, compression, encryption, (IndexEntryType)entryType));
        }

        reader.ReadEndArray();
        return entries;
    }

    private static void WriteBody(CanonicalCborWriter writer, IndexDelta delta, byte[]? signature)
    {
        // Always present: writer_id, sequence, generation, covered_blob_ids,
        // entries — five keys; everything else is conditional.
        var keyCount = 5
            + (delta.PredecessorDeltaId is not null ? 1 : 0)
            + (delta.Shard is not null ? 1 : 0)
            + (delta.IsVoid ? 1 : 0)
            + (signature is not null ? 1 : 0)
            + (delta.CoveredBlobDigests.Count > 0 ? 1 : 0);

        writer.WriteStartMap(keyCount);
        writer.WriteKey(1);
        writer.WriteByteString(delta.WriterId.ToArray());
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(delta.Sequence);

        if (delta.PredecessorDeltaId is { } predecessor)
        {
            writer.WriteKey(3);
            writer.WriteByteString(predecessor.ToArray());
        }

        writer.WriteKey(4);
        writer.WriteUnsignedInteger(delta.Generation);

        if (delta.Shard is { } shard)
        {
            writer.WriteKey(5);
            writer.WriteUnsignedInteger(shard);
        }

        writer.WriteKey(6);
        writer.WriteStartArray(delta.CoveredBlobIds.Count);
        foreach (var blobId in delta.CoveredBlobIds)
        {
            writer.WriteByteString(blobId.ToArray());
        }

        writer.WriteEndArray();
        writer.WriteKey(7);
        WriteEntries(writer, delta.Entries);

        if (delta.IsVoid)
        {
            writer.WriteKey(8);
            writer.WriteBoolean(true);
        }

        if (signature is not null)
        {
            writer.WriteKey(9);
            writer.WriteByteString(signature);
        }

        // Key 10 sorts after the signature and is nonetheless signed: the
        // signature covers every key except itself (07 §2), which is what
        // lets a later key be signed without renumbering an established one.
        if (delta.CoveredBlobDigests.Count > 0)
        {
            writer.WriteKey(10);
            writer.WriteStartArray(delta.CoveredBlobDigests.Count);
            foreach (var digest in delta.CoveredBlobDigests)
            {
                writer.WriteByteString(digest.Span);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndMap();
    }
}

/// <summary>An index object violates specification 07 — a damage finding on read, a caller bug on write.</summary>
public sealed class IndexFormatException : FormatException
{
    /// <summary>Creates the exception; the message names the violated rule.</summary>
    public IndexFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public IndexFormatException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public IndexFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The ADR-0022 §Decision 6 shard-hash rule:
/// <c>shard_hashes[i]</c> = SHA-256 over the deterministic CBOR array of
/// shard <c>shard_set[i]</c>'s post-precedence entries, sorted by object
/// identifier bytes ascending — reader-recomputable from the checkpoint's
/// own entries.
/// </summary>
public static class ShardHashes
{
    /// <summary>Computes the parallel shard_set / shard_hashes arrays for a checkpoint's entries.</summary>
    public static (IReadOnlyList<ushort> ShardSet, IReadOnlyList<byte[]> Hashes) Compute(IReadOnlyList<IndexEntry> entries)
    {
        ThrowHelper.ThrowIfNull(entries);

        var byShard = entries
            .GroupBy(entry => entry.Shard)
            .OrderBy(group => group.Key)
            .ToList();

        var shardSet = new List<ushort>(byShard.Count);
        var hashes = new List<byte[]>(byShard.Count);

        foreach (var group in byShard)
        {
            var sorted = group
                .OrderBy(entry => entry.ObjectId.ToArray(), ByteArrayComparer.Instance)
                .ToList();

            var writer = new CanonicalCborWriter();
            IndexDeltaCodec.WriteEntries(writer, sorted);

            shardSet.Add(group.Key);
            hashes.Add(SHA256.HashData(writer.Encode()));
        }

        return (shardSet, hashes);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
