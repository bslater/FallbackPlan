using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// NFR-PERF-014: the repository-side index costs no more than 80 stored
/// bytes per distinct object once a checkpoint has subsumed the deltas.
/// </summary>
/// <remarks>
/// <para>
/// This is the structure that grew when physical location moved out of
/// manifests and into the index ([ADR-0007](../../../docs/adr/0007-logical-object-identifiers-in-manifests.md)),
/// and nothing bounded it before. It is measured on the sealed object as it
/// sits in the store — after encoding, signing and encryption — because
/// that is what a repository actually pays for, and an in-memory entry count
/// would not have noticed a framing that doubled it.
/// </para>
/// <para>
/// A checkpoint's size has two terms and only one of them scales. The
/// entries scale with the object count; the <c>shard_hashes</c> table does
/// not — [07 §8](../../../specifications/repository-format/07-index.md)
/// fixes 65 536 shards, so a fully populated table is a constant ~2.4 MiB
/// however large the repository grows. Both are measured: the marginal cost
/// of one more object is what the requirement bounds, and the total at half
/// a million objects is what a repository that size actually pays.
/// </para>
/// <para>
/// Below roughly 200 000 objects the fixed table dominates and the total
/// exceeds 80 bytes per object — 104 at four thousand. That is inherent in
/// choosing 65 536 shards, and it is not what the requirement is about: a
/// repository with four thousand objects has a 426 KiB index, which is
/// nobody's problem. The number that would be a problem is the marginal one,
/// and it is flat.
/// </para>
/// </remarks>
public sealed class IndexSizeTests : IDisposable
{
    private const int BytesPerObjectBound = 80;

    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-index-size-tests", Guid.NewGuid().ToString("n"));

    /// <summary>
    /// One entry per distinct object, keyed on a hash of the ordinal so the
    /// identifiers spread across the shard space the way a real repository's
    /// do. A run of adjacent values would land in a handful of shards and
    /// understate the table this exists to account for.
    /// </summary>
    private static IndexEntry Entry(int ordinal)
    {
        var objectBytes = SHA256.HashData(BitConverter.GetBytes(ordinal));

        // One blob per 512 objects, roughly what a real publication produces.
        var blobBytes = SHA256.HashData(BitConverter.GetBytes(ordinal / 512))[..16];

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(blobBytes),
            PhysicalOffset: (ulong)(ordinal * 4_096),
            StoredLength: 65_536,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    [Fact]
    public async Task A_checkpoint_costs_no_more_than_eighty_stored_bytes_per_distinct_object()
    {
        const int smaller = 131_072;
        const int larger = 524_288;

        var small = await MeasureCheckpointAsync(smaller, generation: 1);
        var large = await MeasureCheckpointAsync(larger, generation: 2);

        // The marginal cost: what one more distinct object adds, with the
        // fixed shard table cancelling out of both sides.
        var marginal = (large - small) / (double)(larger - smaller);
        var total = large / (double)larger;

        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"marginal {marginal:f1} B/object; total at {larger} objects {total:f1} B/object.");

        Assert.True(marginal <= BytesPerObjectBound, $"An additional object costs too much index. {detail}");
        Assert.True(total <= BytesPerObjectBound, $"The whole index is over the bound at scale. {detail}");
    }

    private async Task<long> MeasureCheckpointAsync(int objects, ulong generation)
    {
        var label = objects.ToString(CultureInfo.InvariantCulture);
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "store", label));
        using var hierarchy = new KeyHierarchy(MasterKey);
        var sequence = new WriterSequence(
            new FileSequenceStateStore(Path.Combine(_root, "state", label, "sequence.txt")));

        var entries = new List<IndexEntry>(objects);
        for (var ordinal = 0; ordinal < objects; ordinal++)
        {
            entries.Add(Entry(ordinal));
        }

        // Distinctness is the denominator, so it is checked rather than
        // assumed — a generator that collided would flatter the result.
        Assert.Equal(objects, entries.Select(entry => entry.ObjectId).Distinct().Count());

        CheckpointId checkpointId;
        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, sequence))
        {
            var delta = await publisher.PublishDeltaAsync(generation, [], entries, CancellationToken.None);
            checkpointId = await publisher.PublishCheckpointAsync(
                generation,
                subsumedDeltaIds: [delta],
                writerWatermarks: [new WriterWatermark(Writer, 1)],
                entries: entries,
                predecessor: null,
                CancellationToken.None);
        }

        var metadata = await store.GetMetadataAsync(
            MetadataStoreKeys.IndexCheckpoint(generation, checkpointId), CancellationToken.None);

        Assert.True(metadata.Found);
        return metadata.Metadata!.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
