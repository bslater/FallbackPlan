using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// The writer that builds checkpoints (FR-MAN-008, FR-MAN-009; specification
/// 07 §5; ADR-0097). Once enough deltas sit outside every checkpoint, it reads
/// them back from its own store, keeps each one that opens and verifies, and
/// publishes them verbatim in as many checkpoints as the metadata limit
/// needs. A delta it cannot open stays out, so a checkpoint never carries
/// what no reader could read, and the loader goes on reporting that delta as
/// it always did.
/// </summary>
/// <remarks>
/// Nothing here deletes a delta. This class does not establish FR-MAN-017.
/// </remarks>
[TestClass]
public sealed class IndexCheckpointerTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-index-checkpointer-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence Sequence() =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", "sequence.txt")));

    private static IndexEntry Entry(int ordinal)
    {
        var objectBytes = new byte[32];
        BitConverter.GetBytes(ordinal).CopyTo(objectBytes, 0);
        objectBytes[31] = 0x5A;

        return new IndexEntry(
            ObjectId.FromBytes(objectBytes),
            BlobId.FromBytes(Enumerable.Repeat((byte)(ordinal % 200), 16).ToArray()),
            PhysicalOffset: 88,
            StoredLength: 4096,
            CompressionProfileValue: 0x0001,
            EncryptionProfileValue: 0x0001,
            IndexEntryType.Insertion);
    }

    private static async Task<List<DeltaId>> PublishDeltasAsync(IObjectStore store, WriterSequence sequence, int count, int first = 0, int entriesEach = 1)
    {
        var published = new List<DeltaId>();
        using var publisher = new IndexPublisher(store, Repo, Writer, TestAuthority.Shared.Credential, sequence, highestIndexGenerationSeen: 0);
        for (var index = 0; index < count; index++)
        {
            var entries = Enumerable.Range((first + index) * entriesEach, entriesEach).Select(Entry).ToList();
            published.Add(await publisher.PublishDeltaAsync(0, [], entries, CancellationToken.None));
        }

        return published;
    }

    private static IndexCheckpointer Checkpointer(IObjectStore store, WriterSequence sequence, int? byteBudget = null) =>
        byteBudget is { } budget
            ? new IndexCheckpointer(store, Repo, Writer, TestAuthority.Shared.Credential, sequence) { ByteBudget = budget }
            : new IndexCheckpointer(store, Repo, Writer, TestAuthority.Shared.Credential, sequence);

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var loader = new IndexLoader(store, Repo, TestAuthority.Shared.Credential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    private static int DeltaReads(CountingObjectStore store) =>
        store.ReadKeys.Count(key => key.StartsWith("index/delta/", StringComparison.Ordinal));

    [TestMethod]
    public async Task FewerUncoveredDeltasThanTheThreshold_PublishNothing()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 3);

        var counting = new CountingObjectStore(store);
        using var checkpointer = Checkpointer(counting, sequence);
        var published = await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None);

        Assert.IsEmpty(published);
        Assert.AreEqual(0, DeltaReads(counting), "Below the threshold the listing decides, and no delta is read.");
        Assert.IsEmpty((await LoadAsync(store)).Checkpoints);
    }

    [TestMethod]
    public async Task AtTheThreshold_EveryUncoveredDeltaIsCarried_AndALoadFetchesNone()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 4);
        var before = await LoadAsync(store);

        using (var checkpointer = Checkpointer(store, sequence))
        {
            Assert.ContainsSingle(await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None));
        }

        var counting = new CountingObjectStore(store);
        var after = await LoadAsync(counting);

        Assert.IsEmpty(after.Findings);
        var checkpoint = Assert.ContainsSingle(after.Checkpoints).Checkpoint;
        Assert.IsTrue(checkpoint.SubsumedDeltaIds.ToHashSet().SetEquals(deltas));
        Assert.HasCount(deltas.Count, checkpoint.SubsumedDeltas);
        Assert.AreEqual(0, DeltaReads(counting));
        Assert.IsTrue(before.AllEntries.ToHashSet().SetEquals(after.AllEntries));
    }

    [TestMethod]
    public async Task ASecondRun_CarriesOnlyWhatTheFirstDidNot()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 4);
        using (var checkpointer = Checkpointer(store, sequence))
        {
            _ = await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None);
        }

        var later = await PublishDeltasAsync(store, sequence, count: 4, first: 4);
        using (var checkpointer = Checkpointer(store, sequence))
        {
            Assert.ContainsSingle(await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None));
            Assert.IsEmpty(
                await checkpointer.CheckpointAsync(0, threshold: 1, CancellationToken.None),
                "A run with nothing uncovered carries nothing.");
        }

        var counting = new CountingObjectStore(store);
        var state = await LoadAsync(counting);

        Assert.HasCount(2, state.Checkpoints);
        Assert.IsTrue(state.Checkpoints.Any(checkpoint => checkpoint.Checkpoint.SubsumedDeltaIds.ToHashSet().SetEquals(later)));
        Assert.AreEqual(0, DeltaReads(counting));
        Assert.HasCount(8, state.Resolved);
    }

    [TestMethod]
    public async Task ADeltaThatDoesNotOpen_IsNeverCarried_AndIsReportedAsBefore()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 5);

        // Damage one delta where it lies.
        var key = MetadataStoreKeys.IndexDelta(0, deltas[2]);
        byte[] bytes;
        using (var read = await store.OpenReadAsync(key, range: null, CancellationToken.None))
        using (var memory = new MemoryStream())
        {
            await read.Content!.CopyToAsync(memory);
            bytes = memory.ToArray();
        }

        bytes[^1] ^= 0xFF;
        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
        await store.PutAsync(
            key, _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)), PutConditions.IfNotExists,
            CancellationToken.None);

        using (var checkpointer = Checkpointer(store, sequence))
        {
            Assert.ContainsSingle(await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None));
        }

        var state = await LoadAsync(store);
        var checkpoint = Assert.ContainsSingle(state.Checkpoints).Checkpoint;

        Assert.DoesNotContain(deltas[2], checkpoint.SubsumedDeltaIds);
        Assert.HasCount(4, checkpoint.SubsumedDeltaIds);
        Assert.ContainsSingle(state.Findings.Where(finding => finding.Kind == DamageKind.CorruptRecord));
    }

    [TestMethod]
    public async Task OverItsBudget_ItPublishesSeveralCheckpoints_EachWithinIt()
    {
        var store = CreateStore();
        var sequence = Sequence();
        var deltas = await PublishDeltasAsync(store, sequence, count: 6, entriesEach: 8);

        const int budget = 3 * 1024;
        using (var checkpointer = Checkpointer(store, sequence, budget))
        {
            Assert.IsGreaterThan(1, (await checkpointer.CheckpointAsync(0, threshold: 6, CancellationToken.None)).Count);
        }

        var counting = new CountingObjectStore(store);
        var state = await LoadAsync(counting);

        foreach (var checkpoint in state.Checkpoints)
        {
            Assert.IsLessThanOrEqualTo(budget, checkpoint.SignedBytes.Length);
        }

        Assert.IsTrue(state.Checkpoints.SelectMany(checkpoint => checkpoint.Checkpoint.SubsumedDeltaIds).ToHashSet().SetEquals(deltas));
        Assert.AreEqual(0, DeltaReads(counting));
    }

    [TestMethod]
    public async Task ADeltaLargerThanTheBudget_IsNeverCarried_NorCountedTowardTheThreshold()
    {
        var store = CreateStore();
        var sequence = Sequence();
        _ = await PublishDeltasAsync(store, sequence, count: 1, entriesEach: 200);
        _ = await PublishDeltasAsync(store, sequence, count: 3, first: 1000);

        var counting = new CountingObjectStore(store);
        using var checkpointer = Checkpointer(counting, sequence, byteBudget: 4 * 1024);

        Assert.IsEmpty(
            await checkpointer.CheckpointAsync(0, threshold: 4, CancellationToken.None),
            "Three deltas fit a checkpoint and the fourth never will, so four are not due.");
        Assert.AreEqual(0, DeltaReads(counting), "The listing's lengths say so without reading any delta.");
    }

    [TestMethod]
    public void TheDefaults_AreSixtyFourDeltas_AndTheMetadataLimitLessTheHeaderMargin()
    {
        Assert.AreEqual(64, IndexCheckpointer.DefaultThreshold);
        Assert.AreEqual(FormatLimits.MaxMetadataObjectSize - (64 * 1024), IndexCheckpointer.DefaultByteBudget);
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
