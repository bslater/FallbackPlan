using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The B3 acceptance criterion, verbatim (specification 09 §5–§6;
/// FR-ARCH-004, FR-MAN-006): changing one segment of an <em>n</em>-segment file
/// writes exactly one record. Positional comparison against the prior version's
/// in-memory content identifiers decides reuse; mismatched segmentation
/// parameters forbid it — which is FR-MAN-006's requirement that a reusable
/// segment is only resolved <em>within</em> its segmentation profile.
/// </summary>
public sealed class SecondBackupReuseTests : ArchiveTestHarness
{
    [Fact]
    public async Task Changing_one_segment_writes_exactly_one_record()
    {
        var original = BuildTestFile();
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var firstSource = new MemoryStream(original);
        var first = await archiver.ArchiveAsync(firstSource, CancellationToken.None);

        // Change bytes inside exactly one 64 KiB segment.
        var modified = (byte[])original.Clone();
        var victimSegment = 17;
        modified[victimSegment * 64 * 1024 + 100] ^= 0xFF;
        modified[victimSegment * 64 * 1024 + 200] ^= 0xFF;

        using var secondSource = new MemoryStream(modified);
        var second = await archiver.ArchiveAsync(secondSource, first, CancellationToken.None);

        // Exactly one record, in exactly one new blob.
        Assert.Equal(1, second.RecordsWritten);
        Assert.Single(second.Blobs);
        Assert.Equal(first.SegmentReferences.Count, second.SegmentReferences.Count);

        // Every unchanged position reuses the prior reference verbatim.
        for (var index = 0; index < second.SegmentReferences.Count; index++)
        {
            if (index == victimSegment)
            {
                Assert.NotEqual(first.SegmentReferences[index].ObjectId, second.SegmentReferences[index].ObjectId);
            }
            else
            {
                Assert.Equal(first.SegmentReferences[index], second.SegmentReferences[index]);
            }
        }

        // The second version restores byte-identical from the store, which
        // now holds both runs' blobs.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(second.SegmentReferences, restored, CancellationToken.None);

        Assert.True(restore.Success, restore.FailureDetail);
        Assert.Equal(modified, restored.ToArray());
    }

    [Fact]
    public async Task An_unchanged_file_writes_no_records_at_all()
    {
        var original = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var firstSource = new MemoryStream(original);
        var first = await archiver.ArchiveAsync(firstSource, CancellationToken.None);

        using var secondSource = new MemoryStream(original);
        var second = await archiver.ArchiveAsync(secondSource, first, CancellationToken.None);

        Assert.Equal(0, second.RecordsWritten);
        Assert.Empty(second.Blobs);
        Assert.Equal(first.SegmentReferences, second.SegmentReferences);
        Assert.Equal(first.WholeFileHash, second.WholeFileHash);
    }

    [Fact]
    public async Task An_appended_tail_writes_only_the_tail_records()
    {
        var original = BuildTestFile(regions: 6); // 768 KiB — 12 exact segments
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var firstSource = new MemoryStream(original);
        var first = await archiver.ArchiveAsync(firstSource, CancellationToken.None);

        var appended = new byte[original.Length + 10_000];
        original.CopyTo(appended, 0);
        appended[^1] = 0x42;

        using var secondSource = new MemoryStream(appended);
        var second = await archiver.ArchiveAsync(secondSource, first, CancellationToken.None);

        Assert.Equal(1, second.RecordsWritten);
        Assert.Equal(first.SegmentReferences.Count + 1, second.SegmentReferences.Count);
        Assert.Equal(first.SegmentReferences, second.SegmentReferences.Take(first.SegmentReferences.Count));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(second.SegmentReferences, restored, CancellationToken.None);

        Assert.True(restore.Success, restore.FailureDetail);
        Assert.Equal(appended, restored.ToArray());
    }

    [Fact]
    public async Task A_changed_segment_size_forbids_reuse_and_re_archives_in_full()
    {
        var original = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();

        var archiver = CreateArchiver(store, keys);
        using var firstSource = new MemoryStream(original);
        var first = await archiver.ArchiveAsync(firstSource, CancellationToken.None);

        // Same file, double the segment size: content identifiers cannot be
        // compared across parameters (specification 09 §5) — everything is
        // re-archived under the new geometry.
        var rescaled = new FileArchiver(
            SmallBlobPolicy with { SegmentSize = SegmentSize.Create(128 * 1024) },
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            store,
            new MonotonicBlobCounterAllocator(1_000),
            SpoolDirectory);

        using var secondSource = new MemoryStream(original);
        var second = await rescaled.ArchiveAsync(secondSource, first, CancellationToken.None);

        // Re-archived in full under the new geometry — no cross-parameter
        // reuse — with one record per distinct segment (09 §5, §6).
        Assert.Equal(second.SegmentContentIds.Distinct().Count(), second.RecordsWritten);
        Assert.Equal(first.SegmentReferences.Count / 2, second.SegmentReferences.Count);
    }
}
