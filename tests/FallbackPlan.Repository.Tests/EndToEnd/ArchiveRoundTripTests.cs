using System.Text.RegularExpressions;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The slice's proof: a test file is split into fixed-v1 segments, hashed,
/// threshold-compressed, encrypted into records, sealed into multiple blobs,
/// uploaded to the local object store — then restored through recovery
/// footers alone, content-verified segment by segment, and compared
/// byte-identical (FR-ARCH-001, FR-ARCH-003, FR-ARCH-005, FR-ARCH-009,
/// FR-ARCH-012, FR-ARCH-006, FR-RST-002).
/// </summary>
/// <remarks>
/// This exercises the record path and container, <b>not publication
/// semantics</b>: no write intent precedes these uploads and no snapshot is
/// published. Write intents and publication ordering (specification 08)
/// arrive with Wave D, before any real publication engine exists.
/// </remarks>
[TestClass]
public sealed partial class ArchiveRoundTripTests : ArchiveTestHarness
{
    [GeneratedRegex("^blobs/data/[a-z2-7]{4}/[a-z2-7]{26}$")]
    private static partial Regex BlobKeyShape();

    [TestMethod]
    public async Task A_test_file_is_split_backed_up_and_restored_byte_identical()
    {
        var original = BuildTestFile();
        var store = CreateStore();
        using var keys = CreateKeys();

        // Split and back up.
        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(original);
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);

        Assert.AreEqual(original.LongLength, result.LogicalLength);
        Assert.AreEqual(original.Length / (64 * 1024), result.SegmentReferences.Count);
        Assert.IsTrue(result.Blobs.Count > 1, "The small blob targets must force multiple blobs.");

        // One record per DISTINCT segment: identical content derives an
        // identical object identifier and is stored once (09 §6) — the test
        // file's repeating regions make several segments byte-identical.
        Assert.AreEqual(result.SegmentContentIds.Distinct().Count(), result.RecordsWritten);

        // Every stored object sits at blobs/data/<shard>/<store-blob-key>,
        // and the shard is the rendering's first four characters.
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.MatchesRegex(BlobKeyShape(), entry.Key.Value);
            Assert.StartsWith(entry.Key.Components[2], entry.Key.Components[3], StringComparison.Ordinal);
        }

        // Restore through footers alone: a fresh reader with no state from
        // the archive run beyond the logical references a manifest will
        // eventually carry.
        using var reader = new RepositoryReader(Repo, keys, store);
        Assert.AreEqual(result.Blobs.Count, await reader.LoadBlobsAsync(CancellationToken.None));

        // The threshold decision went both ways across the mixed regions.
        Assert.Contains(record => record.CompressionProfileValue == CompressionProfile.ZstdV1.Value, reader.AllRecords);
        Assert.Contains(record => record.CompressionProfileValue == CompressionProfile.None.Value, reader.AllRecords);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(result.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        Assert.AreEqual(original.LongLength, restore.Length);
        SequenceAssert.AreEqual(original, restored.ToArray());
        SequenceAssert.AreEqual(result.WholeFileHash, restore.WholeFileHash);
    }

    [TestMethod]
    public async Task An_empty_file_produces_no_segments_no_records_and_no_blobs()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var source = new MemoryStream();
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);

        // A zero-length file has an empty segment list and no records at all
        // (specification 09 §2.1) — its manifest will say so; nothing is stored.
        Assert.IsEmpty(result.SegmentReferences);
        Assert.IsEmpty(result.Blobs);
        Assert.AreEqual(0, result.LogicalLength);

        using var reader = new RepositoryReader(Repo, keys, store);
        Assert.AreEqual(0, await reader.LoadBlobsAsync(CancellationToken.None));

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(result.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success);
        Assert.AreEqual(0, restore.Length);
        Assert.IsEmpty(restored.ToArray());
    }

    [TestMethod]
    public async Task Each_segment_is_individually_readable_by_object_identifier()
    {
        var original = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var source = new MemoryStream(original);
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Single-segment reads — the targetable-recovery shape E2 will need.
        var reference = result.SegmentReferences[3];
        var segment = await reader.ReadSegmentAsync(reference.ObjectId, CancellationToken.None);

        Assert.AreEqual(RecordReadOutcome.Ok, segment.Outcome);
        SequenceAssert.AreEqual(
            original.AsSpan((int)reference.LogicalOffset, (int)reference.LogicalLength).ToArray(),
            segment.Plaintext);
    }
}
