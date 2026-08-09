using FallbackPlan.TestSupport;
namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The B6 acceptance shape (specification 09 §3.2, §5–§6; FR-ARCH-014,
/// FR-ARCH-004): under <c>cdc-v1</c>, an insertion perturbs only its
/// neighbourhood — the second backup rewrites the affected segments and
/// reuses everything else by content, position notwithstanding.
/// </summary>
[TestClass]
public sealed class CdcSecondBackupTests : ArchiveTestHarness
{
    private static byte[] BuildRandomFile(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [TestMethod]
    public async Task CdcBackup_BytesInsertedAtTheFront_RewritesOnlyTheFirstSegment()
    {
        var original = BuildRandomFile(2_000_000, seed: 20260803);
        var store = CreateStore();
        using var keys = CreateKeys();

        var archiver = CreateArchiver(store, keys, CdcPolicy, firstCounter: 1);
        using var firstSource = new MemoryStream(original);
        var first = await archiver.ArchiveAsync(firstSource, CancellationToken.None);

        Assert.IsTrue(first.SegmentReferences.Count > 5, "the file must span several cdc segments");

        // Prepend one byte: every boundary shifts by one, but every window —
        // and therefore every segment's content — beyond the first cut is
        // unchanged (09 §3.2). fixed-v1 would rewrite the whole file here.
        var inserted = new byte[original.Length + 1];
        inserted[0] = 0x5A;
        original.CopyTo(inserted, 1);

        var second = await CreateArchiver(store, keys, CdcPolicy, firstCounter: 1_000)
            .ArchiveAsync(new MemoryStream(inserted), first, CancellationToken.None);

        // Only the first segment's content changed; everything downstream is
        // reused by content identifier despite the one-byte position shift.
        Assert.AreEqual(1, second.RecordsWritten);
        Assert.AreEqual(first.SegmentReferences.Count, second.SegmentReferences.Count);
        SequenceAssert.AreEqual(
            first.SegmentReferences.Select(reference => reference.ObjectId).Skip(1),
            second.SegmentReferences.Select(reference => reference.ObjectId).Skip(1));

        // The restored second version is byte-identical to the inserted file.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(second.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(inserted, restored.ToArray());
    }

    [TestMethod]
    public async Task CdcBackup_ParametersChangedSinceTheLastRun_ForbidsReuseAndReArchivesInFull()
    {
        var original = BuildRandomFile(1_200_000, seed: 99);
        var store = CreateStore();
        using var keys = CreateKeys();

        var first = await CreateArchiver(store, keys, CdcPolicy, firstCounter: 1)
            .ArchiveAsync(new MemoryStream(original), CancellationToken.None);

        // Same bytes, double the target: content identifiers cannot be
        // compared across parameters (09 §5) — everything is re-archived.
        var rescaled = CdcPolicy with
        {
            CdcParameters = Domain.CdcParameters.Create(128 * 1024, 16 * 1024, 512 * 1024),
        };

        var second = await CreateArchiver(store, keys, rescaled, firstCounter: 2_000)
            .ArchiveAsync(new MemoryStream(original), first, CancellationToken.None);

        Assert.AreEqual(second.SegmentReferences.Count, second.RecordsWritten);
    }

    [TestMethod]
    public async Task CdcBackup_TheFileIsUnchanged_WritesNoRecords()
    {
        var original = BuildRandomFile(1_000_000, seed: 5);
        var store = CreateStore();
        using var keys = CreateKeys();

        var first = await CreateArchiver(store, keys, CdcPolicy, firstCounter: 1)
            .ArchiveAsync(new MemoryStream(original), CancellationToken.None);
        var second = await CreateArchiver(store, keys, CdcPolicy, firstCounter: 3_000)
            .ArchiveAsync(new MemoryStream(original), first, CancellationToken.None);

        Assert.AreEqual(0, second.RecordsWritten);
        Assert.IsEmpty(second.Blobs);
        SequenceAssert.AreEqual(first.WholeFileHash, second.WholeFileHash);
    }
}
