using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FsCheck.Xunit;

namespace FallbackPlan.Repository.FuzzTests;

/// <summary>
/// Portability round-trips over generated structures (NFR-PORT-003): for any
/// value a writer can legitimately produce, encode → decode → re-encode is
/// byte-identical, so no information rides outside the canonical encoding.
/// </summary>
public sealed class RoundTripPropertyTests
{
    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static ObjectId ObjectFrom(int seed) =>
        ObjectId.FromBytes(SHA256.HashData(BitConverter.GetBytes(seed)));

    private static BlobId BlobFrom(int seed) =>
        BlobId.FromBytes(SHA256.HashData(BitConverter.GetBytes(~seed)).AsSpan(0, 16));

    [Property(MaxTest = 100)]
    public void File_version_manifests_round_trip_byte_identically(byte[]? segmentSeeds, bool sparse)
    {
        // Build a legitimate manifest: contiguous references whose lengths
        // are derived from the generated seeds, coverage exact by
        // construction.
        var lengths = (segmentSeeds ?? [1])
            .Take(64)
            .Select(seed => 1 + (seed * 517))
            .DefaultIfEmpty(1)
            .ToArray();

        var references = new List<SegmentReference>(lengths.Length);
        var offset = 0L;
        foreach (var length in lengths)
        {
            references.Add(new SegmentReference(offset, length, ObjectFrom((int)offset)));
            offset += length;
        }

        // A sparse hole rides after the stored pieces: references and sparse
        // extents together must cover [0, logical_length) exactly (06 §3.2).
        var manifest = new FileVersionManifest
        {
            EntryKind = EntryKind.File,
            Name = "generated.bin"u8.ToArray(),
            NameNormalisation = NameNormalisation.Nfc,
            LogicalLength = (ulong)offset + (sparse ? 4096UL : 0),
            SegmentReferences = references,
            WholeFileHash = SHA256.HashData(segmentSeeds ?? []),
            SegmentationProfile = 0x0001,
            SparseExtents = sparse ? [new SparseExtent((ulong)offset, 4096)] : [],
            Metadata = new EntryMetadata { ModifiedAt = 1_722_600_000_000 },
        };

        var encoded = FileVersionManifestCodec.Encode(manifest);
        var decoded = FileVersionManifestCodec.Decode(encoded);

        Assert.Equal(encoded, FileVersionManifestCodec.Encode(decoded));
    }

    [Property(MaxTest = 100)]
    public void Index_deltas_round_trip_byte_identically(byte entryCount, ulong sequence, bool isVoid)
    {
        var entries = Enumerable.Range(0, isVoid ? 0 : 1 + entryCount % 32)
            .Select(index => new IndexEntry(
                ObjectFrom(index),
                BlobFrom(index / 4),
                PhysicalOffset: 88 + (ulong)index * 4096,
                StoredLength: 4096,
                CompressionProfileValue: 0x0001,
                EncryptionProfileValue: 0x0001,
                IndexEntryType.Insertion))
            .ToList();

        var delta = new IndexDelta
        {
            WriterId = Writer,
            Sequence = sequence,
            Generation = 0,
            CoveredBlobIds = entries.Select(entry => entry.BlobId).Distinct().ToList(),
            Entries = entries,
            IsVoid = isVoid,
        };

        var encoded = IndexDeltaCodec.Encode(delta, new byte[64]);
        var decoded = IndexDeltaCodec.Decode(encoded);

        Assert.Equal(encoded, IndexDeltaCodec.Encode(decoded.Delta, decoded.Signature.Span));

        // The signed prefix is the same bytes EncodeForSigning produces —
        // the two-pass construction (06 §6.1) loses nothing.
        Assert.Equal(IndexDeltaCodec.EncodeForSigning(decoded.Delta), decoded.SignedBytes.ToArray());
    }

    [Property(MaxTest = 100)]
    public void Checkpoints_round_trip_byte_identically(byte entryCount, ulong generation)
    {
        var entries = Enumerable.Range(0, 1 + entryCount % 24)
            .Select(index => new IndexEntry(
                ObjectFrom(index),
                BlobFrom(index),
                PhysicalOffset: 88,
                StoredLength: 1024,
                CompressionProfileValue: 0x0001,
                EncryptionProfileValue: 0x0001,
                IndexEntryType.Insertion))
            .ToList();

        var (shardSet, hashes) = ShardHashes.Compute(entries);
        var checkpoint = new Checkpoint
        {
            Generation = generation,
            WriterId = Writer,
            WriterWatermarks = [new WriterWatermark(Writer, generation)],
            ShardSet = shardSet,
            ShardHashesList = [.. hashes.Select(hash => (ReadOnlyMemory<byte>)hash)],
            Entries = entries,
        };

        var encoded = CheckpointCodec.Encode(checkpoint, new byte[64]);
        var decoded = CheckpointCodec.Decode(encoded);

        Assert.Equal(encoded, CheckpointCodec.Encode(decoded.Checkpoint, decoded.Signature.Span));
    }

    [Property(MaxTest = 100)]
    public void Journal_records_of_every_kind_round_trip_byte_identically(
        byte kindSeed, ulong sequence, ulong timestamp, byte blobCount)
    {
        var blobs = Enumerable.Range(0, 1 + blobCount % 16).Select(BlobFrom).ToList();
        JournalPayload payload = (kindSeed % 4) switch
        {
            0 => new JournalPayload.WriteIntent(
                Enumerable.Repeat((byte)0x33, 16).ToArray(), blobs, 60_000, 5, IntentPurpose.Backup),
            1 => new JournalPayload.IntentExtension(sequence / 2, blobs, 90_000),
            2 => new JournalPayload.IntentRetirement(sequence / 2, IntentOutcome.Completed),
            _ => new JournalPayload.Audit(AuditAction.GcPass, "operator@example", 42),
        };
        var kind = (kindSeed % 4) switch
        {
            0 => JournalRecordKind.WriteIntent,
            1 => JournalRecordKind.IntentExtension,
            2 => JournalRecordKind.IntentRetirement,
            _ => JournalRecordKind.Audit,
        };

        var record = new JournalRecord(kind, Writer, sequence, timestamp, payload);

        var encoded = JournalRecordCodec.Encode(record, new byte[64]);
        var decoded = JournalRecordCodec.Decode(encoded);

        Assert.Equal(encoded, JournalRecordCodec.Encode(decoded.Record, decoded.Signature.Span));
    }

    [Property(MaxTest = 200)]
    public void Base32_encodes_then_decodes_to_the_original_bytes(byte[]? bytes)
    {
        bytes ??= [];
        var text = Base32.Encode(bytes);
        var decoded = new byte[bytes.Length + 8];

        Assert.True(Base32.TryDecode(text, decoded, out var written));
        Assert.Equal(bytes, decoded.AsSpan(0, written).ToArray());
    }
}
