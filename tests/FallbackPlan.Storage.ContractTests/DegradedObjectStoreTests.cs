using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Holds the capability-withholding instrument honest
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md)): a store
/// that declares less must also <em>do</em> less, or every gate written over
/// it passes for the wrong reason. Establishes NFR-PORT-004 for the
/// instrument.
/// </summary>
/// <remarks>
/// Does not establish NFR-COMP-005: nothing here says what the engine should
/// do with a store like this — that is the admission gate's to establish.
/// </remarks>
[TestClass]
public sealed class DegradedObjectStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-degraded-tests", Guid.NewGuid().ToString("n"));

    private static Func<CancellationToken, ValueTask<Stream>> Content(params byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private DegradedObjectStore Store(StoreCapabilities capabilities) =>
        new(new LocalFileSystemObjectStore(_root), capabilities);

    [TestMethod]
    public async Task WithoutRangedReads_ARangedReadIsAFaultRatherThanAnOutcome()
    {
        // There is no result for "this provider never offered that", and
        // inventing one would put a capability check on the data path, which
        // architecture 05 §3 exists to keep it off. Asking for what was never
        // declared is a caller fault.
        var store = Store(new StoreCapabilities { ConditionalCreate = true, RangedReads = false });
        var key = ObjectKey.Parse("blobs/data/aaaa/whole-only");
        await store.PutAsync(key, Content(0x01, 0x02, 0x03, 0x04), PutConditions.IfNotExists, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
            await store.OpenReadAsync(key, new ObjectRange(1, 2), CancellationToken.None));

        // The whole-object read still works: only the range was withheld.
        using var whole = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, whole.Outcome);
    }

    [TestMethod]
    public async Task WithoutConditionalCreate_ASecondPutOverwritesAndSaysItCreated()
    {
        // This is the hazard the admission gate exists for, made visible: the
        // engine publishes with IfNotExists and reads Created as proof that
        // nothing was there. Against a store without the primitive, Created
        // means only that a write happened — and the bytes that were there
        // are gone. INV-BLOB-001 is broken by the provider, silently.
        var store = Store(new StoreCapabilities { ConditionalCreate = false, RangedReads = true });
        var key = ObjectKey.Parse("blobs/data/aaaa/sealed-blob");

        Assert.AreEqual(
            PutOutcome.Created,
            (await store.PutAsync(key, Content(0x01), PutConditions.IfNotExists, CancellationToken.None)).Outcome);

        Assert.AreEqual(
            PutOutcome.Created,
            (await store.PutAsync(key, Content(0x02), PutConditions.IfNotExists, CancellationToken.None)).Outcome);

        using var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer, CancellationToken.None);
        SequenceAssert.AreEqual(new byte[] { 0x02 }, buffer.ToArray());
    }

    [TestMethod]
    public async Task WithConditionalCreate_ItBehavesExactlyAsTheInnerStoreDoes()
    {
        // The instrument withholds what it is told to and nothing else; a
        // decorator that degraded a capability it was given would make every
        // negative case above unfalsifiable.
        var store = Store(new StoreCapabilities { ConditionalCreate = true, RangedReads = true });
        var key = ObjectKey.Parse("blobs/data/aaaa/sealed-blob");

        await store.PutAsync(key, Content(0x01), PutConditions.IfNotExists, CancellationToken.None);

        Assert.AreEqual(
            PutOutcome.AlreadyExists,
            (await store.PutAsync(key, Content(0x02), PutConditions.IfNotExists, CancellationToken.None)).Outcome);

        using var read = await store.OpenReadAsync(key, new ObjectRange(0, 1), CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);
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
