using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// A load that races a delta's retirement loses nothing (FR-MAN-017; FR-MAN-008;
/// specification 07 §5.2). A delta is deleted only after a checkpoint carrying
/// it is durable at the same store, so a reader that lists the deltas before
/// the checkpoints either fetches the delta from its key or finds it gone,
/// and a delta found gone is looked for in the checkpoints a fresh listing
/// shows. Only a delta that no checkpoint carries, even then, is reported.
/// </summary>
/// <remarks>
/// Each race is placed exactly: the retirement runs when the reader's first
/// listing of one plane has ended and before it looks at the next.
/// </remarks>
[TestClass]
public sealed class RetiredDeltaLoadTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-retired-delta-load-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence Sequence() =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", "sequence.txt")));

    private static IndexEntry Entry(int ordinal)
    {
        var objectBytes = new byte[32];
        BitConverter.GetBytes(ordinal).CopyTo(objectBytes, 0);
        objectBytes[31] = 0x6B;

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(Enumerable.Repeat((byte)(ordinal % 200), 16).ToArray()),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    private static async Task<List<DeltaId>> PublishDeltasAsync(IObjectStore store, WriterSequence sequence, int count, int first = 0)
    {
        var published = new List<DeltaId>();
        using var publisher = new IndexPublisher(store, Repo, Writer, TestAuthority.Shared.Credential, sequence, highestIndexGenerationSeen: 0);
        for (var index = 0; index < count; index++)
        {
            published.Add(await publisher.PublishDeltaAsync(0, [], [Entry(first + index)], CancellationToken.None));
        }

        return published;
    }

    private static async Task CheckpointAsync(IObjectStore store, WriterSequence sequence)
    {
        using var checkpointer = new IndexCheckpointer(store, Repo, Writer, TestAuthority.Shared.Credential, sequence);
        Assert.ContainsSingle(await checkpointer.CheckpointAsync(0, threshold: 1, CancellationToken.None));
    }

    private static async Task DeleteAsync(LocalFileSystemObjectStore store, DeltaId deltaId) =>
        Assert.AreEqual(
            DeleteOutcome.Deleted,
            (await store.DeleteAsync(MetadataStoreKeys.IndexDelta(0, deltaId), DeleteConditions.None, CancellationToken.None)).Outcome);

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var loader = new IndexLoader(store, Repo, TestAuthority.Shared.Credential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    private static int CheckpointReads(CountingObjectStore store) =>
        store.ReadKeys.Count(key => key.StartsWith("index/checkpoint/", StringComparison.Ordinal));

    [TestMethod]
    public async Task ADeltaRetiredAfterTheCheckpointListing_IsReadFromTheCheckpointAListingAfterItShows()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        var later = await PublishDeltasAsync(store, sequence, count: 4, first: 4);

        var racing = new InterleavingObjectStore(store, "index/checkpoint/", async () =>
        {
            await CheckpointAsync(store, sequence);
            await DeleteAsync(store, later[1]);
        });
        var counting = new CountingObjectStore(racing);

        var state = await LoadAsync(counting);

        Assert.IsTrue(racing.Fired);
        Assert.IsEmpty(state.Findings);
        Assert.HasCount(8, state.Resolved, "The retired delta's entry is applied from the checkpoint that carries it.");
        Assert.HasCount(8, state.AllEntries, "A delta read both from its key and from a carried copy is applied once.");
        Assert.HasCount(2, state.Checkpoints);
        Assert.AreEqual(2, CheckpointReads(counting), "The second listing opens only the checkpoint the first did not show.");
    }

    [TestMethod]
    public async Task ADeltaRetiredAfterTheDeltaListing_IsReadFromTheCheckpointListedAfterIt()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);

        var racing = new InterleavingObjectStore(store, "index/delta/", async () =>
        {
            await CheckpointAsync(store, sequence);
            await DeleteAsync(store, deltas[2]);
        });

        var state = await LoadAsync(racing);

        Assert.IsTrue(racing.Fired);
        Assert.IsEmpty(state.Findings);
        Assert.HasCount(4, state.Resolved);
        Assert.HasCount(4, state.AllEntries);
        Assert.ContainsSingle(state.Checkpoints);
    }

    [TestMethod]
    public async Task AListedDeltaThatVanishes_AndThatNoCheckpointCarries_IsReported()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 3);

        var racing = new InterleavingObjectStore(store, "index/checkpoint/", () => DeleteAsync(store, deltas[1]));

        var state = await LoadAsync(racing);

        Assert.IsTrue(racing.Fired);
        var finding = Assert.ContainsSingle(state.Findings);
        Assert.AreEqual(DamageKind.MissingIndexObject, finding.Kind);
        Assert.Contains(MetadataStoreKeys.IndexDelta(0, deltas[1]).Value, finding.Detail, StringComparison.Ordinal);
        Assert.HasCount(2, state.Resolved);
    }

    [TestMethod]
    public async Task WhenNoListedDeltaVanishes_TheCheckpointsAreListedOnce()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 4);
        await CheckpointAsync(store, sequence);
        _ = await PublishDeltasAsync(store, sequence, count: 2, first: 4);

        var counting = new CountingObjectStore(store);
        var state = await LoadAsync(counting);

        Assert.IsEmpty(state.Findings);
        Assert.HasCount(6, state.Resolved);
        Assert.AreEqual(1, counting.Listings.Count(prefix => prefix == "index/checkpoint/"));
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
