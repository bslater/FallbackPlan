using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// What the index and journal planes do when a listing has not caught up
/// ([ADR-0012](../../../docs/adr/0012-storage-provider-contract.md); 07 §3,
/// §4). Establishes NFR-COMP-005 and NFR-SEC-005.
/// </summary>
/// <remarks>
/// <para>
/// These are the planes that have a designed defence, as against the
/// collector, which has none and is refused outright
/// (<c>Retention.Tests/EventualListingTests</c>). Deltas form gapless
/// per-writer chains precisely so that a reader can <b>detect</b> one it has
/// not seen rather than assume it has everything, and the writer's sequence
/// is allocated before anything is written so a stale head collides on a
/// conditional put rather than overwriting.
/// </para>
/// <para>
/// One of the three is a limit rather than a defence, and it is written as
/// such: the chain is verified from the watermark to the highest sequence the
/// listing produced, so a <em>hole</em> is found and a <em>truncated tail</em>
/// is not. That is not a hole in the reasoning — it is what "gapless" can mean
/// without a second witness of the true head — and the consequence is bounded,
/// because an object the missing delta located is simply not found.
/// </para>
/// </remarks>
[TestClass]
public sealed class EventualListingIndexTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-eventual-index", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence CreateSequence() =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", "sequence.txt")));

    private static IndexEntry Entry(byte objectSeed, byte blobSeed)
    {
        var objectBytes = new byte[32];
        Array.Fill(objectBytes, objectSeed);
        var blobBytes = new byte[16];
        Array.Fill(blobBytes, blobSeed);

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(blobBytes),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    [TestMethod]
    public async Task ADeltaTheListingHasNotCaughtUpTo_IsDetectedAsAGapRatherThanAssumedAway()
    {
        // Three deltas, the middle one withheld from the listing. The chain
        // is what makes this visible: sequences 1 and 3 are there, so 2 is
        // owed, and the loader says so instead of resolving what it can and
        // reporting success.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = CreateSequence();

        // The key carries a delta id, not a sequence number, so which delta a
        // key names is only knowable from the publish that produced it —
        // picking one by key order would conceal an arbitrary sequence and
        // make this case pass or fail by luck.
        DeltaId middle;
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence))
        {
            await publisher.PublishDeltaAsync(0, [Entry(1, 1).BlobId], [Entry(1, 1)], CancellationToken.None);
            middle = await publisher.PublishDeltaAsync(0, [Entry(2, 2).BlobId], [Entry(2, 2)], CancellationToken.None);
            await publisher.PublishDeltaAsync(0, [Entry(3, 3).BlobId], [Entry(3, 3)], CancellationToken.None);
        }

        Assert.HasCount(3, await ListAsync(store, "index/delta/"));

        var withheld = MetadataStoreKeys.IndexDelta(0, middle).Value;
        var lagging = new LaggingObjectStore(store);
        lagging.Conceal(key => string.Equals(key, withheld, StringComparison.Ordinal));

        using var loader = new IndexLoader(lagging, Repo, credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 2,
            isSequenceAccountedAsync: null, blobState: null, CancellationToken.None);

        // Unresolved, not damage: the patience window is what tells a lag
        // apart from a loss, and two generations have not passed.
        Assert.ContainsSingle(state.UnresolvedGaps);
        Assert.AreEqual(Writer, Assert.ContainsSingle(state.UnresolvedGaps).Writer);
        Assert.IsEmpty(state.Findings);

        // And once the listing catches up the gap closes with nothing else
        // having had to happen.
        lagging.Release();
        using var again = new IndexLoader(lagging, Repo, credential);
        var caughtUp = await again.LoadAsync(0, 2, null, null, CancellationToken.None);
        Assert.IsEmpty(caughtUp.UnresolvedGaps);
        Assert.HasCount(3, caughtUp.Resolved);
    }

    [TestMethod]
    public async Task ATruncatedTail_IsNotDetectable_AndTheObjectItLocatedIsSimplyNotFound()
    {
        // The stated limit. The chain is checked from the watermark up to the
        // highest sequence the listing produced, so a missing *last* delta
        // moves the ceiling instead of leaving a hole under it. Nothing short
        // of a second witness of the writer's true head could tell this from
        // a repository that genuinely stops there.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = CreateSequence();

        DeltaId newest;
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence))
        {
            await publisher.PublishDeltaAsync(0, [Entry(1, 1).BlobId], [Entry(1, 1)], CancellationToken.None);
            newest = await publisher.PublishDeltaAsync(0, [Entry(2, 2).BlobId], [Entry(2, 2)], CancellationToken.None);
        }

        var withheld = MetadataStoreKeys.IndexDelta(0, newest).Value;
        var lagging = new LaggingObjectStore(store);
        lagging.Conceal(key => string.Equals(key, withheld, StringComparison.Ordinal));

        using var loader = new IndexLoader(lagging, Repo, credential);
        var state = await loader.LoadAsync(0, 2, null, null, CancellationToken.None);

        Assert.IsEmpty(state.UnresolvedGaps);
        Assert.IsEmpty(state.Findings);

        // What it costs is bounded and visible at the point of use: the
        // object the withheld delta located is not resolvable, so a read of
        // it fails to find it rather than reading something else.
        Assert.ContainsSingle(state.Resolved);
        Assert.IsFalse(state.Resolved.ContainsKey(Entry(2, 2).ObjectId));
    }

    [TestMethod]
    public async Task AJournalListingThatLags_UnderReportsTheHead_AndTheNextPutCollidesRatherThanOverwriting()
    {
        // The allocator reads the head from a listing, so a lag makes it
        // answer low — and the whole reason a publication allocates before it
        // writes is that the write itself is the check. A conditional put is
        // refused, and the bytes that were there stay there (INV-BLOB-001,
        // specification 01 §4).
        var store = CreateStore();
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 1));
        await PutAsync(store, MetadataStoreKeys.Journal(Writer, 2));

        var lagging = new LaggingObjectStore(store);
        lagging.Conceal(key => key.EndsWith(MetadataStoreKeys.Journal(Writer, 2).Value[^4..], StringComparison.Ordinal));

        var stale = await ObservedHead.JournalHeadAsync(lagging, Writer, CancellationToken.None);
        var truth = await ObservedHead.JournalHeadAsync(store, Writer, CancellationToken.None);

        Assert.AreEqual(2UL, truth);
        Assert.AreEqual(1UL, stale, "a lagging listing must under-report, never over-report");

        // A writer that believed the stale head would allocate 2 again. The
        // put is what refuses it.
        var collision = await lagging.PutAsync(
            MetadataStoreKeys.Journal(Writer, 2),
            _ => ValueTask.FromResult<Stream>(new MemoryStream([9, 9, 9])),
            PutConditions.IfNotExists,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.AlreadyExists, collision.Outcome);

        using var read = await store.OpenReadAsync(
            MetadataStoreKeys.Journal(Writer, 2), range: null, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer, CancellationToken.None);
        SequenceAssert.AreEqual(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    private static async Task PutAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        var result = await store.PutAsync(
            key, _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            PutConditions.IfNotExists, CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, result.Outcome);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
