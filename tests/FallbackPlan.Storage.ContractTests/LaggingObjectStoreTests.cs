using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Holds the eventual-listing instrument honest
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md);
/// architecture 05 §1). Establishes NFR-COMP-005 for the instrument only —
/// what the engine does when a listing lags is
/// <c>Retention.Tests</c>' and <c>Repository.Tests</c>' to establish, not
/// this suite's.
/// </summary>
/// <remarks>
/// A fault injector that quietly became a pass-through would turn every test
/// written over it green for the wrong reason, which is the specific way this
/// kind of instrument fails. So each property it claims is asserted here
/// rather than assumed from its name: reads do not lag, listings do, and
/// releasing is what ends it.
/// </remarks>
[TestClass]
public sealed class LaggingObjectStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-lagging-tests", Guid.NewGuid().ToString("n"));

    private static Func<CancellationToken, ValueTask<Stream>> Content(params byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static async Task<List<string>> ListAsync(LaggingObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    [TestMethod]
    public void Capabilities_OfALaggingStore_SayEventualWhateverTheInnerStoreSays()
    {
        var inner = new LocalFileSystemObjectStore(_root);
        Assert.AreEqual(ListingConsistency.Strong, inner.Capabilities.ListingConsistency);

        var lagging = new LaggingObjectStore(inner);

        Assert.AreEqual(ListingConsistency.Eventual, lagging.Capabilities.ListingConsistency);

        // Everything else is the inner store's: the lag is a listing property
        // and must not quietly withdraw ranged reads or conditional create,
        // or a test written over it would be proving two things at once.
        Assert.AreEqual(inner.Capabilities.RangedReads, lagging.Capabilities.RangedReads);
        Assert.AreEqual(inner.Capabilities.ConditionalCreate, lagging.Capabilities.ConditionalCreate);
    }

    [TestMethod]
    public async Task APutThatHasNotCaughtUp_IsReadableAndNotListable()
    {
        var store = new LaggingObjectStore(new LocalFileSystemObjectStore(_root));
        var key = ObjectKey.Parse("snapshots/dev-1/set-1/newest");

        var put = await store.PutAsync(key, Content(0x01, 0x02), PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        // Read-after-write holds. This is the half that makes the other half
        // dangerous: the object is there, and only an enumerating reader
        // cannot see it.
        var metadata = await store.GetMetadataAsync(key, CancellationToken.None);
        Assert.IsTrue(metadata.Found);
        using var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);

        Assert.IsEmpty(await ListAsync(store, "snapshots/"));
        Assert.AreEqual(1, store.UnlistedCount);

        store.Release();

        Assert.ContainsSingle(await ListAsync(store, "snapshots/"));
        Assert.AreEqual(0, store.UnlistedCount);
    }

    [TestMethod]
    public async Task ADeleteThatHasNotCaughtUp_StopsReadingAndGoesOnBeingListed()
    {
        var store = new LaggingObjectStore(new LocalFileSystemObjectStore(_root));
        var key = ObjectKey.Parse("blobs/data/aaaa/swept");

        await store.PutAsync(key, Content(0x01, 0x02, 0x03), PutConditions.IfNotExists, CancellationToken.None);
        store.Release();

        Assert.AreEqual(
            DeleteOutcome.Deleted,
            (await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None)).Outcome);

        using (var read = await store.OpenReadAsync(key, range: null, CancellationToken.None))
        {
            Assert.AreEqual(OpenReadOutcome.NotFound, read.Outcome);
        }

        // Still listed, and listed at the length it had: a reader that trusts
        // the listing is told an object exists and then cannot read it.
        var listed = await ListAsync(store, "blobs/");
        Assert.AreEqual(key.Value, Assert.ContainsSingle(listed));
        Assert.AreEqual(1, store.LingeringCount);

        store.Release();
        Assert.IsEmpty(await ListAsync(store, "blobs/"));
    }

    [TestMethod]
    public async Task AKeyDeletedBeforeItsPutCaughtUp_IsNeverListedAtAll()
    {
        // It was never visible; being removed does not make it so. Without
        // this the instrument would invent a listing entry for an object no
        // reader could ever have seen.
        var store = new LaggingObjectStore(new LocalFileSystemObjectStore(_root));
        var key = ObjectKey.Parse("blobs/meta/bbbb/short-lived");

        await store.PutAsync(key, Content(0x09), PutConditions.IfNotExists, CancellationToken.None);
        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        Assert.IsEmpty(await ListAsync(store, "blobs/"));
        Assert.AreEqual(0, store.LingeringCount);

        store.Release();
        Assert.IsEmpty(await ListAsync(store, "blobs/"));
    }

    [TestMethod]
    public async Task TheFilter_ScopesTheLagToOnePlane()
    {
        // The planes lag independently, and the states worth testing are the
        // mixed ones: a snapshot listing behind a current blob listing is a
        // different repository from one where both are behind.
        var store = new LaggingObjectStore(
            new LocalFileSystemObjectStore(_root),
            key => key.StartsWith("snapshots/", StringComparison.Ordinal));

        await store.PutAsync(
            ObjectKey.Parse("snapshots/dev-1/set-1/newest"), Content(0x01), PutConditions.IfNotExists, CancellationToken.None);
        await store.PutAsync(
            ObjectKey.Parse("blobs/data/aaaa/its-content"), Content(0x02), PutConditions.IfNotExists, CancellationToken.None);

        Assert.IsEmpty(await ListAsync(store, "snapshots/"));
        Assert.ContainsSingle(await ListAsync(store, "blobs/"));
    }

    [TestMethod]
    public async Task ALaggingListing_KeepsOrdinalOrderAndItsResumePoint()
    {
        // A lingering entry is synthesised rather than served by the inner
        // store, so the two contract properties a listing carries — ordinal
        // order and a resume token that resumes strictly after its entry —
        // have to survive the merge.
        var store = new LaggingObjectStore(new LocalFileSystemObjectStore(_root));
        foreach (var name in new[] { "entry-1", "entry-2", "entry-3" })
        {
            await store.PutAsync(
                ObjectKey.Parse($"index/delta/0000000000000001/{name}"),
                Content(0x01),
                PutConditions.IfNotExists,
                CancellationToken.None);
        }

        store.Release();
        await store.DeleteAsync(
            ObjectKey.Parse("index/delta/0000000000000001/entry-2"), DeleteConditions.None, CancellationToken.None);

        var all = await ListAsync(store, "index/");
        SequenceAssert.AreEqual(
            [
                "index/delta/0000000000000001/entry-1",
                "index/delta/0000000000000001/entry-2",
                "index/delta/0000000000000001/entry-3",
            ],
            all);

        var after = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("index/"),
            new ListOptions { ResumeAfter = "index/delta/0000000000000001/entry-1" },
            CancellationToken.None))
        {
            after.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(
            ["index/delta/0000000000000001/entry-2", "index/delta/0000000000000001/entry-3"],
            after);
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
