using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A load takes a blob its store holds only in an archival tier as what it
/// is (ADR-0104): skipped as archived, neither damage nor a passing fault,
/// and the rest loaded. The targeted load is how a sync's read-back opens the
/// blobs it samples (FR-VER-001). The full load is how retention opens a
/// store, among others, and one archived blob refusing it would leave every
/// snapshot there unread for want of one (NFR-REL-004).
/// </summary>
[TestClass]
public sealed class ArchivedBlobLoadTests : ArchiveTestHarness
{
    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, List<ObjectKey> Blobs)> ArchiveAsync()
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var source = new MemoryStream(BuildTestFile());
        await CreateArchiver(store, keys).ArchiveAsync(source, CancellationToken.None);

        var blobs = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            blobs.Add(entry.Key);
        }

        Assert.IsGreaterThanOrEqualTo(2, blobs.Count, "the archive must span several blobs");
        return (store, keys, blobs);
    }

    [TestMethod]
    public async Task FullLoad_ABlobHeldOnlyInAnArchivalTier_IsSkippedAsArchived_AndTheRestLoads()
    {
        var (store, keys, blobs) = await ArchiveAsync();
        using var held = keys;
        var cold = blobs[0];

        using var reader = new RepositoryReader(
            Repo, keys, new ArchivingObjectStore(store, key => key == cold.Value), Authority);
        var loaded = await reader.LoadBlobsAsync(CancellationToken.None);

        Assert.AreEqual(blobs.Count - 1, loaded);
        var skipped = Assert.ContainsSingle(reader.SkippedBlobs);
        Assert.AreEqual(cold, skipped.Key);
        Assert.IsTrue(skipped.Archived, skipped.Reason);
    }

    [TestMethod]
    public async Task TargetedLoad_ABlobItsStoreSaysIsArchived_IsSkippedAsArchived_WithoutAskingToReadIt()
    {
        var (store, keys, blobs) = await ArchiveAsync();
        using var held = keys;
        var cold = blobs[0];
        var counting = new CountingObjectStore(new ArchivingObjectStore(store, key => key == cold.Value));

        using var reader = new RepositoryReader(Repo, keys, counting, Authority);
        var opened = await reader.LoadBlobsAsync([cold], CancellationToken.None);

        Assert.AreEqual(0, opened);
        Assert.IsTrue(Assert.ContainsSingle(reader.SkippedBlobs).Archived);
        Assert.AreEqual(0, counting.Reads, "the store's answer to the HEAD already says a read would be refused");
    }

    [TestMethod]
    public async Task TargetedLoad_ABlobArchivedBetweenItsHeadAndItsRead_IsSkippedAsArchived()
    {
        var (store, keys, blobs) = await ArchiveAsync();
        using var held = keys;
        var cold = blobs[0];

        using var reader = new RepositoryReader(
            Repo, keys, new ArchivingObjectStore(store, key => key == cold.Value, movedAfterHead: true), Authority);
        var opened = await reader.LoadBlobsAsync(blobs, CancellationToken.None);

        Assert.AreEqual(blobs.Count - 1, opened);
        var skipped = Assert.ContainsSingle(reader.SkippedBlobs);
        Assert.AreEqual(cold, skipped.Key);
        Assert.IsTrue(skipped.Archived, skipped.Reason);
    }
}
