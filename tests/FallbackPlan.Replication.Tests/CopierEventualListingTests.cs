using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// What a replication pass does when a destination's listing has not caught
/// up ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md);
/// architecture 05 §1). Establishes NFR-COMP-005 and FR-REP-005.
/// </summary>
/// <remarks>
/// The copier survives a lag in both directions, and the reason is that it
/// only ever <b>adds</b>. A destination that under-reports what it holds is
/// offered objects it already has, and the conditional put refuses them —
/// wasted requests, nothing lost. One that over-reports has an object skipped
/// this pass and copied on the next, because the decision is recomputed from
/// scratch every time rather than recorded. Neither is free, and neither is
/// damage. That is the contrast with collection, which reasons from absence to
/// deletion and is refused outright
/// (<c>Retention.Tests/EventualListingTests</c>).
/// </remarks>
[TestClass]
public sealed class CopierEventualListingTests
{
    private string _root = null!;
    private string _sourcePath = null!;
    private string _replicaPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-eventual-{Guid.NewGuid():N}");
        _sourcePath = Path.Combine(_root, "source");
        _replicaPath = Path.Combine(_root, "replica");
        Directory.CreateDirectory(_sourcePath);
        Directory.CreateDirectory(_replicaPath);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task ADestinationThatUnderReportsWhatItHolds_IsOfferedThemAgainAndRefusesThem()
    {
        var source = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(source);

        var replicaInner = new LocalFileSystemObjectStore(_replicaPath);
        var first = await StoreToStoreCopier.CopyAsync(source, replicaInner, CancellationToken.None);
        Assert.AreEqual(8L, first.Copied);

        // The replica holds everything and lists none of it — the worst case
        // of an under-reporting listing, and the one a pass cannot detect.
        var lagging = new LaggingObjectStore(replicaInner);
        lagging.Conceal(_ => true);

        var second = await StoreToStoreCopier.CopyAsync(source, lagging, CancellationToken.None);

        // Every object is offered again, and every one is refused by the
        // store rather than written twice. The cost is requests; the outcome
        // is what it already was.
        Assert.AreEqual(8L, second.Examined);
        Assert.AreEqual(0L, second.Copied, "a conditional put is what stops a re-offer becoming a rewrite");
        Assert.AreEqual(8L, second.AlreadyHeld);
    }

    [TestMethod]
    public async Task ADestinationThatOverReportsWhatItHolds_SkipsAnObjectThisPassAndTakesItOnTheNext()
    {
        var source = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(source);

        var replicaInner = new LocalFileSystemObjectStore(_replicaPath);
        await StoreToStoreCopier.CopyAsync(source, replicaInner, CancellationToken.None);

        // A delete the replica's listing has not caught up to: the object is
        // gone and still enumerated, so the source is told it is held.
        var lagging = new LaggingObjectStore(replicaInner);
        var lost = ObjectKey.Parse("blobs/data/0000/object-a");
        Assert.AreEqual(
            DeleteOutcome.Deleted,
            (await lagging.DeleteAsync(lost, DeleteConditions.None, CancellationToken.None)).Outcome);

        var duringTheLag = await StoreToStoreCopier.CopyAsync(source, lagging, CancellationToken.None);
        Assert.AreEqual(0L, duringTheLag.Copied, "the stale listing is believed, which is why this is not a proof of possession");

        using (var missing = await replicaInner.OpenReadAsync(lost, range: null, CancellationToken.None))
        {
            Assert.AreEqual(OpenReadOutcome.NotFound, missing.Outcome);
        }

        // Nothing recorded that the pair was complete, so once the listing
        // catches up the next ordinary pass restores it. This is why a sync
        // record is not evidence of possession and FR-GC-009 will not reclaim
        // a last copy on the strength of one.
        lagging.Release();
        var afterwards = await StoreToStoreCopier.CopyAsync(source, lagging, CancellationToken.None);
        Assert.AreEqual(1L, afterwards.Copied);

        using var restored = await replicaInner.OpenReadAsync(lost, range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, restored.Outcome);
    }

    private static async Task SeedAsync(LocalFileSystemObjectStore source)
    {
        string[] keys =
        [
            "repository-format",
            "blobs/data/0000/object-a",
            "blobs/meta/0000/object-b",
            "journal/writer-a/00000001",
            "index/delta/00000001/delta-a",
            "index/checkpoint/00000001",
            "something-else/object-c",
            "snapshots/device-a/set-a/snapshot-a",
        ];

        foreach (var key in keys)
        {
            var payload = new byte[64];
            Random.Shared.NextBytes(payload);
            await source.PutAsync(
                ObjectKey.Parse(key),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }
    }
}
