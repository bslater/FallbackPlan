using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// A format-5 checkpoint carries the deltas it subsumes, verbatim
/// (FR-MAN-008, FR-MAN-009, FR-MAN-013; specification 07 §5; ADR-0097). Key
/// 10 holds each subsumed delta's stored object, beside its identifier in key
/// 2, and the loader opens each one exactly as it would the object at its
/// key. So a load that reads the checkpoint instead of the deltas reaches the
/// same index: every entry keeps its own rank, writer and sequence, and every
/// covered blob keeps its digest and root. What changes is how many objects
/// it fetches.
/// </summary>
/// <remarks>
/// Nothing here deletes a delta: the loader only stops fetching the ones a
/// checkpoint carries. This class does not establish FR-MAN-017.
/// </remarks>
[TestClass]
public sealed class CheckpointBundleTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Lower =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly WriterId Higher =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-checkpoint-bundle-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", $"{name}.txt")));

    private static IndexEntry Entry(byte objectSeed, byte blobSeed, IndexEntryType type = IndexEntryType.Insertion)
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
            type);
    }

    private static BlobId Blob(byte seed) => BlobId.FromBytes(Enumerable.Repeat(seed, 16).ToArray());

    private static byte[] Digest(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    private static async Task<byte[]> StoredAsync(IObjectStore store, DeltaId deltaId, ulong generation = 0)
    {
        using var read = await store.OpenReadAsync(
            MetadataStoreKeys.IndexDelta(generation, deltaId), range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);
        using var memory = new MemoryStream();
        await read.Content!.CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Two writers' history: insertions, a relocation by the lower writer
    /// that outranks the higher writer's insertion, a void, and digests and
    /// roots on the covered blobs.
    /// </summary>
    private async Task<List<DeltaId>> PublishHistoryAsync(IObjectStore store)
    {
        var published = new List<DeltaId>();
        var lower = Sequence("lower");
        var higher = Sequence("higher");
        using var lowerPublisher = new IndexPublisher(store, Repo, Lower, TestCredential, lower, highestIndexGenerationSeen: 0);
        using var higherPublisher = new IndexPublisher(store, Repo, Higher, TestCredential, higher, highestIndexGenerationSeen: 2);

        published.Add((await lowerPublisher.PublishDeltaDetailedAsync(
            0, [Blob(1)], [Entry(1, 1), Entry(2, 1)], [Digest(1)], [Digest(11)], CancellationToken.None)).DeltaId);
        published.Add((await higherPublisher.PublishDeltaDetailedAsync(
            0, [Blob(2)], [Entry(3, 2), Entry(1, 2)], [Digest(2)], [Digest(12)], CancellationToken.None)).DeltaId);
        published.Add(await higherPublisher.PublishVoidDeltaAsync(0, higher.AllocateNext(), CancellationToken.None));

        // The lower writer relocates object 1 after reading both insertions.
        using var relocating = new IndexPublisher(store, Repo, Lower, TestCredential, lower, highestIndexGenerationSeen: 9);
        published.Add((await relocating.PublishDeltaDetailedAsync(
            0, [Blob(3)], [Entry(1, 3, IndexEntryType.Supersession)], [Digest(3)], [Digest(13)], CancellationToken.None)).DeltaId);

        return published;
    }

    private static RepositoryWriteCredential TestCredential => TestAuthority.Shared.Credential;

    private static async Task<IndexState> LoadAsync(IObjectStore store)
    {
        using var loader = new IndexLoader(store, Repo, TestCredential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    private static void AssertSameIndex(IndexState expected, IndexState actual)
    {
        Assert.HasCount(expected.Resolved.Count, actual.Resolved);
        foreach (var (objectId, winner) in expected.Resolved)
        {
            Assert.IsTrue(actual.Resolved.TryGetValue(objectId, out var other), $"Object {objectId} is not resolved.");
            Assert.AreEqual(winner, other, $"Object {objectId} resolves differently.");
        }

        Assert.IsTrue(
            expected.AllEntries.ToHashSet().SetEquals(actual.AllEntries),
            "The applied entries differ: a bundled delta must contribute exactly what the delta at its key does.");
        SequenceAssert.AreEqual(
            expected.Deltas.Select(delta => $"{delta.Delta.WriterId}:{delta.Delta.Sequence}").Order(StringComparer.Ordinal),
            actual.Deltas.Select(delta => $"{delta.Delta.WriterId}:{delta.Delta.Sequence}").Order(StringComparer.Ordinal));
        SequenceAssert.AreEqual(
            expected.UnresolvedGaps.Select(gap => $"{gap.Writer}:{gap.Sequence}").Order(StringComparer.Ordinal),
            actual.UnresolvedGaps.Select(gap => $"{gap.Writer}:{gap.Sequence}").Order(StringComparer.Ordinal));
        Assert.HasCount(
            expected.Deltas.Sum(delta => delta.Delta.CoveredBlobMerkleRoots.Count),
            actual.Deltas.SelectMany(delta => delta.Delta.CoveredBlobMerkleRoots));
    }

    [TestMethod]
    public void ACheckpointCarryingDeltas_RoundTrips_AndKeyTenIsSigned()
    {
        using var signer = RepositorySigner.Create(TestCredential, KeyGeneration.Zero);
        var checkpoint = new Checkpoint
        {
            Generation = 0,
            WriterId = Lower,
            SubsumedDeltaIds = [DeltaId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray())],
            SubsumedDeltas = [new byte[] { 1, 2, 3, 4 }],
        };

        var signedBytes = CheckpointCodec.EncodeForSigning(checkpoint);
        var decoded = CheckpointCodec.Decode(CheckpointCodec.Encode(checkpoint, signer.Sign(signedBytes)));

        Assert.HasCount(1, decoded.Checkpoint.SubsumedDeltas);
        SequenceAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, decoded.Checkpoint.SubsumedDeltas[0].ToArray());
        Assert.IsTrue(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));

        // Key 10 sorts after the signature and is signed anyway, so a store
        // that swapped a bundled delta would break the checkpoint's signature.
        var tampered = checkpoint with { SubsumedDeltas = [new byte[] { 1, 2, 3, 5 }] };
        Assert.IsFalse(signer.Verify(CheckpointCodec.EncodeForSigning(tampered), decoded.Signature.Span));
    }

    [TestMethod]
    public void ACheckpoint_WhoseBundledDeltasAndIdentifiersDisagreeInCount_IsRefused()
    {
        var checkpoint = new Checkpoint
        {
            Generation = 0,
            WriterId = Lower,
            SubsumedDeltaIds = [DeltaId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray()), DeltaId.FromBytes(Enumerable.Repeat((byte)8, 16).ToArray())],
            SubsumedDeltas = [new byte[] { 1 }],
        };

        Assert.ThrowsExactly<IndexFormatException>(() => CheckpointCodec.EncodeForSigning(checkpoint));
    }

    [TestMethod]
    public void ACheckpointCarryingDeltas_AlsoCarryingEntriesOrWatermarks_IsRefused()
    {
        // The two forms do not mix. Entries a checkpoint carried for itself
        // would rank at the checkpoint, which is what the bundled form exists
        // to avoid (ADR-0008 Amendment 1, constraint 1).
        var bundled = new Checkpoint
        {
            Generation = 0,
            WriterId = Lower,
            SubsumedDeltaIds = [DeltaId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray())],
            SubsumedDeltas = [new byte[] { 1 }],
        };

        Assert.ThrowsExactly<IndexFormatException>(() => CheckpointCodec.EncodeForSigning(bundled with
        {
            Entries = [Entry(1, 1)],
            ShardSet = [Entry(1, 1).Shard],
            ShardHashesList = [Digest(9)],
        }));
        Assert.ThrowsExactly<IndexFormatException>(() => CheckpointCodec.EncodeForSigning(bundled with
        {
            WriterWatermarks = [new WriterWatermark(Lower, 1)],
        }));
    }

    [TestMethod]
    public async Task ALoadThroughACheckpoint_ReachesTheSameIndex_AndFetchesNoDeltaItCarries()
    {
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);
        Assert.IsEmpty(deltasOnly.Findings);

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            List<BundledDelta> bundled = [];
            foreach (var deltaId in deltas)
            {
                bundled.Add(new BundledDelta(deltaId, await StoredAsync(store, deltaId)));
            }

            await publisher.PublishBundledCheckpointAsync(0, bundled, predecessor: null, CancellationToken.None);
        }

        var counting = new CountingObjectStore(store);
        var throughCheckpoint = await LoadAsync(counting);

        Assert.IsEmpty(throughCheckpoint.Findings);
        Assert.ContainsSingle(throughCheckpoint.Checkpoints);
        AssertSameIndex(deltasOnly, throughCheckpoint);

        // The relocation still wins: the bundled entries rank as their deltas
        // did, not at the checkpoint's writer.
        Assert.AreEqual(Blob(3), throughCheckpoint.Resolved[Entry(1, 0).ObjectId].Entry.BlobId);

        Assert.IsEmpty(
            counting.ReadKeys.Where(key => key.StartsWith("index/delta/", StringComparison.Ordinal)),
            "Every delta the checkpoint carries was fetched anyway: " + string.Join(", ", counting.ReadKeys));
    }

    [TestMethod]
    public async Task ALoadThroughACheckpointCarryingSomeDeltas_FetchesOnlyTheRest()
    {
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            await publisher.PublishBundledCheckpointAsync(
                0,
                [new BundledDelta(deltas[0], await StoredAsync(store, deltas[0])), new BundledDelta(deltas[1], await StoredAsync(store, deltas[1]))],
                predecessor: null,
                CancellationToken.None);
        }

        var counting = new CountingObjectStore(store);
        var partly = await LoadAsync(counting);

        Assert.IsEmpty(partly.Findings);
        AssertSameIndex(deltasOnly, partly);
        Assert.HasCount(
            deltas.Count - 2, counting.ReadKeys.Where(key => key.StartsWith("index/delta/", StringComparison.Ordinal)));

        // Each delta keeps its own identifier whichever way it was read: from
        // its key, or from key 2 beside its copy.
        Assert.IsTrue(partly.Deltas.Select(delta => delta.DeltaId).ToHashSet().SetEquals(deltas));
        Assert.IsTrue(deltasOnly.Deltas.Select(delta => delta.DeltaId).ToHashSet().SetEquals(deltas));
    }

    [TestMethod]
    public async Task ABundledDeltaThatDoesNotOpen_IsReported_AndTheDeltaAtItsKeyIsReadInstead()
    {
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        var damaged = await StoredAsync(store, deltas[0]);
        damaged[^1] ^= 0xFF; // inside the authentication tag

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            await publisher.PublishBundledCheckpointAsync(
                0, [new BundledDelta(deltas[0], damaged)], predecessor: null, CancellationToken.None);
        }

        var state = await LoadAsync(store);

        Assert.ContainsSingle(state.Findings.Where(finding => finding.Kind == DamageKind.CorruptRecord));
        AssertSameIndex(deltasOnly, state);
    }

    [TestMethod]
    public async Task ADeltaACheckpointCarries_IsApplied_EvenWhenTheListingNoLongerShowsIt()
    {
        // A listing that omits a delta cannot hide it once a checkpoint
        // carries it: the copy inside is the delta (07 §5).
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            List<BundledDelta> bundled = [];
            foreach (var deltaId in deltas)
            {
                bundled.Add(new BundledDelta(deltaId, await StoredAsync(store, deltaId)));
            }

            await publisher.PublishBundledCheckpointAsync(0, bundled, predecessor: null, CancellationToken.None);
        }

        await store.DeleteAsync(MetadataStoreKeys.IndexDelta(0, deltas[1]), DeleteConditions.None, CancellationToken.None);

        var state = await LoadAsync(store);

        Assert.IsEmpty(state.Findings);
        AssertSameIndex(deltasOnly, state);
    }

    [TestMethod]
    public async Task TwoCheckpointsCarryingOneDelta_ApplyItOnce()
    {
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        var stored = await StoredAsync(store, deltas[0]);
        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            await publisher.PublishBundledCheckpointAsync(0, [new BundledDelta(deltas[0], stored)], predecessor: null, CancellationToken.None);
            await publisher.PublishBundledCheckpointAsync(0, [new BundledDelta(deltas[0], stored)], predecessor: null, CancellationToken.None);
        }

        var state = await LoadAsync(store);

        Assert.IsEmpty(state.Findings);
        Assert.HasCount(2, state.Checkpoints);
        AssertSameIndex(deltasOnly, state);
        Assert.HasCount(deltasOnly.AllEntries.Count, state.AllEntries);
    }

    [TestMethod]
    public async Task AWatermark_HidesNoDelta()
    {
        // The earlier form dropped every delta at or below a watermark
        // without reading subsumed_delta_ids, so a builder whose listing
        // missed one hid it for good (ADR-0008 Amendment 1, constraint 3).
        // A watermark now states nothing a loader acts on.
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            await publisher.PublishCheckpointAsync(
                0,
                subsumedDeltaIds: [],
                writerWatermarks: [new WriterWatermark(Lower, 99), new WriterWatermark(Higher, 99)],
                entries: [],
                predecessor: null,
                CancellationToken.None);
        }

        var state = await LoadAsync(store);

        AssertSameIndex(deltasOnly, state);
    }

    [TestMethod]
    public async Task TheEarlierFormsEntries_AreNotApplied()
    {
        // Entries a checkpoint carried for itself ranked at the checkpoint's
        // writer and watermark, which outranked deltas published after them
        // (ADR-0008 Amendment 1, constraint 1). Nothing ever published one;
        // a loader that met one would rank it wrongly, so it ranks it nowhere.
        var store = CreateStore();
        _ = await PublishHistoryAsync(store);
        var deltasOnly = await LoadAsync(store);

        using (var publisher = new IndexPublisher(store, Repo, Higher, TestCredential, Sequence("checkpoint")))
        {
            await publisher.PublishCheckpointAsync(
                0,
                subsumedDeltaIds: [],
                writerWatermarks: [],
                entries: [Entry(1, 9), Entry(77, 9)],
                predecessor: null,
                CancellationToken.None);
        }

        var state = await LoadAsync(store);

        AssertSameIndex(deltasOnly, state);
        Assert.IsFalse(state.Resolved.ContainsKey(Entry(77, 0).ObjectId));
    }

    [TestMethod]
    public async Task ACatalogueRebuiltThroughACheckpoint_LocatesWhatTheDeltasDo_AndAppliesNoEarlierFormEntry()
    {
        var store = CreateStore();
        var deltas = await PublishHistoryAsync(store);

        using (var publisher = new IndexPublisher(store, Repo, Lower, TestCredential, Sequence("checkpoint")))
        {
            List<BundledDelta> bundled = [];
            foreach (var deltaId in deltas)
            {
                bundled.Add(new BundledDelta(deltaId, await StoredAsync(store, deltaId)));
            }

            await publisher.PublishBundledCheckpointAsync(0, bundled, predecessor: null, CancellationToken.None);
            await publisher.PublishCheckpointAsync(
                0, subsumedDeltaIds: [], writerWatermarks: [], entries: [Entry(77, 9)], predecessor: null,
                CancellationToken.None);
        }

        var counting = new CountingObjectStore(store);
        using var loader = new IndexLoader(counting, Repo, TestCredential);
        using var catalogue = Catalogue.Catalogue.Open(Path.Combine(_root, "catalogue.db"), Repo);
        var report = await new Catalogue.CatalogueRebuilder(loader).RebuildAsync(
            catalogue, currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null,
            CancellationToken.None);

        Assert.IsEmpty(report.Findings);
        Assert.AreEqual(deltas.Count, report.DeltasApplied);
        Assert.AreEqual(Blob(3), catalogue.ResolveLocation(Entry(1, 0).ObjectId)!.BlobId);
        Assert.AreEqual(Blob(2), catalogue.ResolveLocation(Entry(3, 0).ObjectId)!.BlobId);
        Assert.IsNull(catalogue.ResolveLocation(Entry(77, 0).ObjectId), "An earlier-form entry was ranked.");
        Assert.IsEmpty(counting.ReadKeys.Where(key => key.StartsWith("index/delta/", StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
