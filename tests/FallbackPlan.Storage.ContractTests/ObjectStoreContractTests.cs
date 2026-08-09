using System.Text;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The reusable provider contract suite (docs/architecture/05-storage-providers.md
/// §2; FR-REP-002, NFR-PORT-002, NFR-PORT-004): every <see cref="IObjectStore"/>
/// implementation inherits this and must pass unchanged. Expected outcomes are
/// asserted as results, never as exceptions; the content factory is asserted
/// as re-invocable, which is what makes retry possible at all.
/// </summary>
public abstract class ObjectStoreContractTests
{
    /// <summary>Creates a fresh, empty store for one test.</summary>
    protected abstract IObjectStore CreateStore();

    private static Func<CancellationToken, ValueTask<Stream>> ContentFactory(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static async Task<byte[]> ReadAllAsync(OpenReadResult result)
    {
        Assert.AreEqual(OpenReadOutcome.Found, result.Outcome);

        using (result)
        {
            using var buffer = new MemoryStream();
            await result.Content!.CopyToAsync(buffer, CancellationToken.None);
            return buffer.ToArray();
        }
    }

    [TestMethod]
    public async Task PutThenOpenRead_AnyContent_RoundTripsTheBytes()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("blobs/data/abcd/roundtrip");
        var payload = Encoding.ASCII.GetBytes("segment record bytes");

        var put = await store.PutAsync(key, ContentFactory(payload), PutConditions.None, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        SequenceAssert.AreEqual(payload, await ReadAllAsync(read));
    }

    [TestMethod]
    public async Task OpenRead_GivenARange_ReturnsExactlyThatSlice()
    {
        var store = CreateStore();

        if (!store.Capabilities.RangedReads)
        {
            return;
        }

        var key = ObjectKey.Parse("blobs/data/abcd/ranged");
        var payload = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();
        await store.PutAsync(key, ContentFactory(payload), PutConditions.None, CancellationToken.None);

        var read = await store.OpenReadAsync(key, new ObjectRange(100, 250), CancellationToken.None);

        SequenceAssert.AreEqual(payload[100..350], await ReadAllAsync(read));
    }

    [TestMethod]
    public async Task OpenRead_RangeStartsPastTheEnd_ReportsRangeNotSatisfiableAsAResult()
    {
        var store = CreateStore();

        if (!store.Capabilities.RangedReads)
        {
            return;
        }

        var key = ObjectKey.Parse("blobs/data/abcd/short");
        await store.PutAsync(key, ContentFactory(new byte[10]), PutConditions.None, CancellationToken.None);

        using var read = await store.OpenReadAsync(key, new ObjectRange(5, 10), CancellationToken.None);

        Assert.AreEqual(OpenReadOutcome.RangeNotSatisfiable, read.Outcome);
    }

    [TestMethod]
    public async Task GetMetadata_AnExistingObject_ReportsItsLength()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("index/delta/0000000000000001/meta");
        await store.PutAsync(key, ContentFactory(new byte[123]), PutConditions.None, CancellationToken.None);

        var metadata = await store.GetMetadataAsync(key, CancellationToken.None);

        Assert.IsTrue(metadata.Found);
        Assert.AreEqual(123, metadata.Metadata!.Length);
    }

    [TestMethod]
    public async Task GetMetadata_AMissingObject_ReportsNotFoundAsAResult()
    {
        var store = CreateStore();

        var metadata = await store.GetMetadataAsync(ObjectKey.Parse("absent"), CancellationToken.None);

        Assert.IsFalse(metadata.Found);
    }

    [TestMethod]
    public async Task OpenRead_AMissingObject_ReportsNotFoundAsAResult()
    {
        var store = CreateStore();

        using var read = await store.OpenReadAsync(ObjectKey.Parse("absent"), range: null, CancellationToken.None);

        Assert.AreEqual(OpenReadOutcome.NotFound, read.Outcome);
    }

    [TestMethod]
    public async Task Put_SameKeyASecondTime_ReportsAlreadyExistsAsAResult()
    {
        // The doc's own scenario (§2.2): an idempotent retry of a write that
        // in fact succeeded is the most common expected outcome.
        var store = CreateStore();
        var key = ObjectKey.Parse("snapshots/device/set/snap-1");
        var payload = "published snapshot"u8.ToArray();

        var first = await store.PutAsync(key, ContentFactory(payload), PutConditions.IfNotExists, CancellationToken.None);
        var second = await store.PutAsync(key, ContentFactory(payload), PutConditions.IfNotExists, CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, first.Outcome);
        Assert.AreEqual(PutOutcome.AlreadyExists, second.Outcome);

        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        SequenceAssert.AreEqual(payload, await ReadAllAsync(read));
    }

    [TestMethod]
    public async Task Put_DifferentContentUnderALiveKey_LeavesTheStoredObjectUnchanged()
    {
        // INV-BLOB-001 (specification 05 §5.1) and the general rule it
        // specialises (01 §4). The idempotent-retry case above re-puts
        // identical bytes; this is the one that matters, because a store
        // that quietly accepted it would break nonce uniqueness, make a
        // published blob digest meaningless, and hand a concurrent reader a
        // different object at the offsets it already holds.
        var store = CreateStore();
        var key = ObjectKey.Parse("blobs/data/aaaa/sealed-blob");
        var sealedBytes = "the bytes that were sealed"u8.ToArray();

        Assert.AreEqual(
            PutOutcome.Created,
            (await store.PutAsync(key, ContentFactory(sealedBytes), PutConditions.IfNotExists, CancellationToken.None)).Outcome);

        foreach (var conditions in new[] { PutConditions.IfNotExists, PutConditions.None })
        {
            var rewrite = await store.PutAsync(
                key, ContentFactory("different bytes entirely"u8.ToArray()), conditions, CancellationToken.None);

            // Reported as a result, not thrown — and, either way, not applied.
            Assert.AreEqual(PutOutcome.AlreadyExists, rewrite.Outcome);
        }

        var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        SequenceAssert.AreEqual(sealedBytes, await ReadAllAsync(read));
    }

    [TestMethod]
    public async Task Put_AnyContent_InvokesTheFactoryAndDisposesItsStream()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("journal/writer/0000000000000001");
        var invocations = 0;
        TrackingStream? lastStream = null;

        var put = await store.PutAsync(
            key,
            _ =>
            {
                invocations++;
                lastStream = new TrackingStream("intent record"u8.ToArray());
                return ValueTask.FromResult<Stream>(lastStream);
            },
            PutConditions.None,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        Assert.IsTrue(invocations >= 1, "The factory must be invoked at least once.");
        Assert.IsTrue(lastStream!.Disposed, "The provider owns and disposes each stream the factory produces.");
    }

    [TestMethod]
    public async Task Put_WhenTheProviderRetries_InvokesTheFactoryAgainWithoutError()
    {
        // The contract half of §2.1: the caller guarantees the factory can
        // produce the content again, and the provider may call it as many
        // times as its retry policy requires. This proves a multi-invocation
        // factory works; fault-injected retries arrive with Wave F.
        var store = CreateStore();
        var key = ObjectKey.Parse("blobs/meta/abcd/refetchable");
        var invocations = 0;

        Func<CancellationToken, ValueTask<Stream>> factory = _ =>
        {
            invocations++;
            return ValueTask.FromResult<Stream>(new MemoryStream("same bytes every time"u8.ToArray()));
        };

        _ = await factory(CancellationToken.None);
        var put = await store.PutAsync(key, factory, PutConditions.None, CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    public async Task List_GivenAPrefix_ReturnsMatchingKeysInOrdinalOrder()
    {
        var store = CreateStore();
        var keys = new[] { "blobs/data/zz/2", "blobs/data/aa/1", "blobs/meta/aa/3", "index/delta/0000000000000001/d" };

        foreach (var value in keys)
        {
            await store.PutAsync(ObjectKey.Parse(value), ContentFactory([0x01]), PutConditions.None, CancellationToken.None);
        }

        var listed = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(["blobs/data/aa/1", "blobs/data/zz/2", "blobs/meta/aa/3"], listed);
    }

    [TestMethod]
    public async Task List_PrefixEndsMidComponent_StillMatchesByString()
    {
        var store = CreateStore();
        var keys = new[]
        {
            "hints/identity/aaaa/aaaabbbb/0000000000000001/s",
            "hints/identity/aaab/aaabcccc/0000000000000002/s",
            "hints/identity/aaaa/aaaadddd/0000000000000003/s",
            "hints/placement/aaaa",
        };

        foreach (var value in keys)
        {
            await store.PutAsync(ObjectKey.Parse(value), ContentFactory([0x01]), PutConditions.None, CancellationToken.None);
        }

        // A prefix is a string over the flat namespace and need not stop at a
        // component boundary. An implementation that narrows its walk to the
        // directory a prefix names has to keep answering this, because the
        // last component may be a fragment of a name rather than a directory.
        var listed = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("hints/identity/aaa"), ListOptions.Default, CancellationToken.None))
        {
            listed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(
            [
                "hints/identity/aaaa/aaaabbbb/0000000000000001/s",
                "hints/identity/aaaa/aaaadddd/0000000000000003/s",
                "hints/identity/aaab/aaabcccc/0000000000000002/s",
            ],
            listed);

        // And a prefix that names a whole directory sees only that directory.
        var narrowed = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("hints/identity/aaaa/aaaabbbb/"), ListOptions.Default, CancellationToken.None))
        {
            narrowed.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(["hints/identity/aaaa/aaaabbbb/0000000000000001/s"], narrowed);

        // A prefix naming nothing lists nothing, rather than everything.
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("hints/identity/zzzz/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.Fail($"'{entry.Key}' matched a prefix nothing sits under.");
        }
    }

    [TestMethod]
    public async Task List_ResumeTokenPersistedAcrossEnumerators_ResumesStrictlyAfterItsEntry()
    {
        var store = CreateStore();

        for (var i = 0; i < 5; i++)
        {
            await store.PutAsync(
                ObjectKey.Parse($"index/delta/0000000000000001/entry-{i}"),
                ContentFactory([0x01]),
                PutConditions.None,
                CancellationToken.None);
        }

        string? resumeToken = null;
        var firstPass = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/"), ListOptions.Default, CancellationToken.None))
        {
            firstPass.Add(entry.Key.Value);
            if (firstPass.Count == 2)
            {
                resumeToken = entry.ResumeToken;
                break;
            }
        }

        // A brand-new enumeration — a different process, conceptually —
        // resumes strictly after the persisted position.
        var secondPass = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("index/"),
            ListOptions.Default with { ResumeAfter = resumeToken },
            CancellationToken.None))
        {
            secondPass.Add(entry.Key.Value);
        }

        SequenceAssert.AreEqual(
            ["index/delta/0000000000000001/entry-2", "index/delta/0000000000000001/entry-3", "index/delta/0000000000000001/entry-4"],
            secondPass);
    }

    [TestMethod]
    public async Task Delete_TheSameKeyTwice_ReportsDeletedThenNotFoundAsResults()
    {
        var store = CreateStore();
        var key = ObjectKey.Parse("tombstones/blob-1");
        await store.PutAsync(key, ContentFactory([0x01]), PutConditions.None, CancellationToken.None);

        var first = await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
        var second = await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        Assert.AreEqual(DeleteOutcome.Deleted, first.Outcome);
        Assert.AreEqual(DeleteOutcome.NotFound, second.Outcome);

        var metadata = await store.GetMetadataAsync(key, CancellationToken.None);
        Assert.IsFalse(metadata.Found);
    }

    /// <summary>A stream that records whether the provider disposed it.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        internal TrackingStream(byte[] bytes)
            : base(bytes, writable: false)
        {
        }

        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
