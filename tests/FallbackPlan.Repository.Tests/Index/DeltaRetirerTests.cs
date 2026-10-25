using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// Retiring the deltas a checkpoint carries (FR-MAN-017; specification 07 §7).
/// A delta is deleted from a store only when that store holds a checkpoint
/// whose copy of it opens and is the very object at the delta's key, and the
/// checkpoint's writer has published again at that store since: the grace is
/// counted in publications, not in time. Checkpoints are never deleted, so
/// whichever checkpoint's authority a delta was retired on, every reader of
/// the store still applies it. A store whose listing may lag retires nothing.
/// </summary>
[TestClass]
public sealed class DeltaRetirerTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"));

    private static readonly WriterId OtherWriter =
        WriterId.FromBytes(Convert.FromHexString("d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-delta-retirer-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence Sequence(string name = "sequence") =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", $"{name}.txt")));

    private static IndexEntry Entry(int ordinal)
    {
        var objectBytes = new byte[32];
        BitConverter.GetBytes(ordinal).CopyTo(objectBytes, 0);
        objectBytes[31] = 0x7C;

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(Enumerable.Repeat((byte)(ordinal % 200), 16).ToArray()),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    private static async Task<List<DeltaId>> PublishDeltasAsync(
        IObjectStore store, WriterSequence sequence, int count, int first = 0, WriterId? writer = null)
    {
        var published = new List<DeltaId>();
        using var publisher = new IndexPublisher(
            store, Repo, writer ?? Writer, TestAuthority.Shared.Credential, sequence, highestIndexGenerationSeen: 0);
        for (var index = 0; index < count; index++)
        {
            published.Add(await publisher.PublishDeltaAsync(0, [], [Entry(first + index)], CancellationToken.None));
        }

        return published;
    }

    private static async Task CheckpointAsync(IObjectStore store, WriterSequence sequence, WriterId? writer = null)
    {
        using var checkpointer = new IndexCheckpointer(store, Repo, writer ?? Writer, TestAuthority.Shared.Credential, sequence);
        Assert.IsNotEmpty(await checkpointer.CheckpointAsync(0, threshold: 1, CancellationToken.None));
    }

    // The writer's next publication, as a backup's first act would be: a
    // journal record whose number is above every checkpoint it published.
    private static async Task PublishAgainAsync(IObjectStore store, WriterSequence sequence, WriterId? writer = null)
    {
        using var journal = new JournalPublisher(store, Repo, writer ?? Writer, TestAuthority.Shared.Credential, sequence);
        _ = await journal.PublishAsync(
            JournalRecordKind.Audit, new JournalPayload.Audit(AuditAction.GcPass, "test", 1), 1000, 0, CancellationToken.None);
    }

    private static async Task<byte[]> ReadAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        using var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);
        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static async Task<HashSet<DeltaId>> DeltasAtTheirKeysAsync(LocalFileSystemObjectStore store)
    {
        var present = new HashSet<DeltaId>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.IsTrue(MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId));
            present.Add(deltaId);
        }

        return present;
    }

    private static async Task<DeltaRetirement> RetireAsync(
        IObjectStore store, Func<DeltaId, bool>? admit = null, CheckpointCoverage? coverage = null)
    {
        using var retirer = coverage is null
            ? new DeltaRetirer(store, Repo, TestAuthority.Shared.Credential)
            : new DeltaRetirer(store, Repo, TestAuthority.Shared.Credential) { Coverage = coverage };
        return await retirer.RetireAsync(admit, CancellationToken.None);
    }

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var loader = new IndexLoader(store, Repo, TestAuthority.Shared.Credential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    [TestMethod]
    public async Task ACarriedDelta_OnceItsCheckpointIsSettled_IsRetired_AndALoadIsUnchanged()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);
        var before = await LoadAsync(store);

        var retirement = await RetireAsync(store);

        Assert.IsTrue(retirement.Retired.ToHashSet().SetEquals(deltas));
        Assert.AreEqual(0, retirement.Unsettled);
        Assert.IsEmpty(await DeltasAtTheirKeysAsync(store));

        var after = await LoadAsync(store);
        Assert.IsEmpty(after.Findings);
        Assert.IsTrue(before.AllEntries.ToHashSet().SetEquals(after.AllEntries));
        Assert.HasCount(4, after.Resolved);
    }

    [TestMethod]
    public async Task ACarriedDelta_WhoseCheckpointsWriterHasNotPublishedSince_IsKept()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);

        var retirement = await RetireAsync(store);

        Assert.IsEmpty(retirement.Retired);
        Assert.AreEqual(4, retirement.Unsettled);
        Assert.IsTrue((await DeltasAtTheirKeysAsync(store)).SetEquals(deltas));
    }

    [TestMethod]
    public async Task ADeltaNoCheckpointCarries_IsKept()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var carried = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        var uncarried = await PublishDeltasAsync(store, sequence, count: 2, first: 4);
        await PublishAgainAsync(store, sequence);

        var retirement = await RetireAsync(store);

        Assert.IsTrue(retirement.Retired.ToHashSet().SetEquals(carried));
        Assert.IsTrue((await DeltasAtTheirKeysAsync(store)).SetEquals(uncarried));
        Assert.HasCount(6, (await LoadAsync(store)).Resolved);
    }

    [TestMethod]
    public async Task ADeltaWhoseCarriedCopyDoesNotOpen_IsKept()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 2);

        var damaged = await ReadAsync(store, MetadataStoreKeys.IndexDelta(0, deltas[0]));
        damaged[^1] ^= 0xFF;
        using (var publisher = new IndexPublisher(store, Repo, Writer, TestAuthority.Shared.Credential, sequence))
        {
            _ = await publisher.PublishBundledCheckpointAsync(
                0,
                [
                    new BundledDelta(deltas[0], damaged),
                    new BundledDelta(deltas[1], await ReadAsync(store, MetadataStoreKeys.IndexDelta(0, deltas[1]))),
                ],
                predecessor: null,
                CancellationToken.None);
        }

        await PublishAgainAsync(store, sequence);

        var retirement = await RetireAsync(store);

        Assert.AreEqual(deltas[1], Assert.ContainsSingle(retirement.Retired));
        Assert.AreEqual(deltas[0], Assert.ContainsSingle(await DeltasAtTheirKeysAsync(store)));
        Assert.HasCount(2, (await LoadAsync(store)).Resolved, "The damaged copy is reported, and the delta read from its key.");
    }

    [TestMethod]
    public async Task ADeltaWhoseKeyHoldsOtherBytesThanTheCopyNamedForIt_IsKept()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 2);

        // A copy that opens, but is not the object at the key it is named
        // for: deleting that key would lose the delta that is there.
        using (var publisher = new IndexPublisher(store, Repo, Writer, TestAuthority.Shared.Credential, sequence))
        {
            _ = await publisher.PublishBundledCheckpointAsync(
                0,
                [new BundledDelta(deltas[1], await ReadAsync(store, MetadataStoreKeys.IndexDelta(0, deltas[0])))],
                predecessor: null,
                CancellationToken.None);
        }

        await PublishAgainAsync(store, sequence);

        var retirement = await RetireAsync(store);

        Assert.IsEmpty(retirement.Retired);
        Assert.IsTrue((await DeltasAtTheirKeysAsync(store)).SetEquals(deltas));
    }

    [TestMethod]
    public async Task ConcurrentCheckpointsCarryingDifferentSets_RetireOnEitherAuthority_AndNoEntryIsLost()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var otherSequence = Sequence("other");
        var deltas = await PublishDeltasAsync(store, sequence, count: 3);
        deltas.AddRange(await PublishDeltasAsync(store, otherSequence, count: 3, first: 3, writer: OtherWriter));
        var before = await LoadAsync(store);

        // Neither writer's checkpointer sees the other's checkpoint or every
        // delta: the first carries deltas 0 to 3, the second 1 to 5.
        var firstView = new LaggingObjectStore(store);
        firstView.Conceal(key =>
            key == MetadataStoreKeys.IndexDelta(0, deltas[4]).Value || key == MetadataStoreKeys.IndexDelta(0, deltas[5]).Value);
        await CheckpointAsync(firstView, sequence);

        var secondView = new LaggingObjectStore(store);
        secondView.Conceal(key =>
            key.StartsWith("index/checkpoint/", StringComparison.Ordinal) ||
            key == MetadataStoreKeys.IndexDelta(0, deltas[0]).Value);
        await CheckpointAsync(secondView, otherSequence, OtherWriter);

        var carriers = (await LoadAsync(store)).Checkpoints
            .Select(checkpoint => checkpoint.Checkpoint.SubsumedDeltaIds.ToHashSet())
            .ToList();
        Assert.HasCount(2, carriers);
        Assert.IsTrue(carriers.Any(set => set.SetEquals(deltas[..4])));
        Assert.IsTrue(carriers.Any(set => set.SetEquals(deltas[1..])));

        await PublishAgainAsync(store, sequence);
        await PublishAgainAsync(store, otherSequence, OtherWriter);

        var retirement = await RetireAsync(store);

        Assert.IsTrue(retirement.Retired.ToHashSet().SetEquals(deltas));
        Assert.IsEmpty(await DeltasAtTheirKeysAsync(store));

        var after = await LoadAsync(store);
        Assert.IsEmpty(after.Findings);
        Assert.IsTrue(before.AllEntries.ToHashSet().SetEquals(after.AllEntries));
    }

    [TestMethod]
    public async Task OnlyTheDeltasTheCallerAdmits_AreRetired()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);

        var retirement = await RetireAsync(store, admit: deltaId => deltaId != deltas[2]);

        Assert.HasCount(3, retirement.Retired);
        Assert.AreEqual(deltas[2], Assert.ContainsSingle(await DeltasAtTheirKeysAsync(store)));
    }

    [TestMethod]
    public async Task AStoreWhoseListingMayLag_RetiresNothing_AndIsNotAskedAnything()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);

        var counting = new CountingObjectStore(
            new DegradedObjectStore(store, store.Capabilities with { ListingConsistency = ListingConsistency.Eventual }));
        var retirement = await RetireAsync(counting);

        Assert.IsEmpty(retirement.Retired);
        Assert.IsEmpty(counting.Listings);
        Assert.AreEqual(0L, counting.Deletes);
        Assert.IsTrue((await DeltasAtTheirKeysAsync(store)).SetEquals(deltas));
    }

    [TestMethod]
    public async Task ACheckpointKnownToCarryNothingStillPresent_IsNotOpenedAgain()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);

        var coverage = new CheckpointCoverage();
        Assert.HasCount(4, (await RetireAsync(store, coverage: coverage)).Retired);

        var later = await PublishDeltasAsync(store, sequence, count: 4, first: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);

        var counting = new CountingObjectStore(store);
        var retirement = await RetireAsync(counting, coverage: coverage);

        Assert.IsTrue(retirement.Retired.ToHashSet().SetEquals(later));
        Assert.AreEqual(
            1,
            counting.ReadKeys.Count(key => key.StartsWith("index/checkpoint/", StringComparison.Ordinal)),
            "Only the checkpoint published since the first pass is opened.");
        Assert.HasCount(8, (await LoadAsync(store)).Resolved);
    }

    [TestMethod]
    public async Task WithNoDeltaAtItsKey_NoCheckpointIsListed()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        await PublishAgainAsync(store, sequence);
        _ = await RetireAsync(store);

        var counting = new CountingObjectStore(store);
        var retirement = await RetireAsync(counting);

        Assert.IsEmpty(retirement.Retired);
        Assert.IsTrue(counting.Listings.SequenceEqual(["index/delta/"]));
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
