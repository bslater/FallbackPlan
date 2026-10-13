using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// Format 4's index generation (FR-MAN-015, FR-MAN-016; specification 07
/// §2, §3, §4; ADR-0096). Every delta a format-4 writer publishes carries
/// key 12: one more than the highest index generation it has seen. Readers
/// rank entries by it, so an entry published after another always outranks
/// it, whichever writer published either. A gap ages in index generations,
/// from the first later delta of its writer that carries one.
/// </summary>
[TestClass]
public sealed class IndexGenerationTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly WriterId Lower =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly WriterId Higher =
        WriterId.FromBytes(Convert.FromHexString("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-index-generation-tests", Guid.NewGuid().ToString("n"));

    private LocalFileSystemObjectStore CreateStore() => new(Path.Combine(_root, "store"));

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(_root, "state", $"{name}.txt")));

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

    private static IndexDelta Delta(ulong? indexGeneration) => new()
    {
        WriterId = Lower,
        Sequence = 7,
        Generation = 0,
        IndexGeneration = indexGeneration,
        CoveredBlobIds = [BlobId.FromBytes(Enumerable.Repeat((byte)6, 16).ToArray())],
        Entries = [Entry(1, 6)],
    };

    [TestMethod]
    public void ADelta_CarriesItsIndexGeneration_AsKeyTwelve_UnderItsSignature()
    {
        using var credential = TestAuthority.Shared.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);

        var delta = Delta(indexGeneration: 7);
        var signedBytes = IndexDeltaCodec.EncodeForSigning(delta);
        var decoded = IndexDeltaCodec.Decode(IndexDeltaCodec.Encode(delta, signer.Sign(signedBytes)));

        Assert.AreEqual(7UL, decoded.Delta.IndexGeneration);
        Assert.IsTrue(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));

        // Key 12 sorts after the signature and is signed anyway, so a store
        // that raised an entry's rank would break the delta's signature.
        Assert.AreNotEqual(
            Convert.ToHexString(signedBytes),
            Convert.ToHexString(IndexDeltaCodec.EncodeForSigning(delta with { IndexGeneration = 8 })));
    }

    [TestMethod]
    public void ADeltaWithoutKeyTwelve_HasNoIndexGeneration()
    {
        // Every delta a format-3 writer published, which a format-4 reader
        // ranks at zero.
        using var credential = TestAuthority.Shared.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);

        var delta = Delta(indexGeneration: null);
        var decoded = IndexDeltaCodec.Decode(
            IndexDeltaCodec.Encode(delta, signer.Sign(IndexDeltaCodec.EncodeForSigning(delta))));

        Assert.IsNull(decoded.Delta.IndexGeneration);
    }

    [TestMethod]
    public void AnIndexGenerationOfZero_IsNeitherWrittenNorRead()
    {
        // Zero is what an absent key means, so a present zero is a second
        // encoding of one fact, which a canonical form does not have.
        Assert.ThrowsExactly<IndexFormatException>(() => IndexDeltaCodec.EncodeForSigning(Delta(indexGeneration: 0)));

        using var credential = TestAuthority.Shared.Credential.Clone();
        using var signer = RepositorySigner.Create(credential, KeyGeneration.Zero);
        var one = Delta(indexGeneration: 1);
        var stored = IndexDeltaCodec.Encode(one, signer.Sign(IndexDeltaCodec.EncodeForSigning(one)));

        // Key 12 is the last key, and 1 encodes as the single byte 0x01.
        Assert.AreEqual(0x0C, stored[^2]);
        Assert.AreEqual(0x01, stored[^1]);
        stored[^1] = 0x00;

        Assert.ThrowsExactly<IndexFormatException>(() => IndexDeltaCodec.Decode(stored));
    }

    [TestMethod]
    public async Task AFormatFourWriter_PublishesOneAboveTheHighestItHasSeen_AndEveryDeltaAdvancesIt()
    {
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = Sequence("advances");

        using (var publisher = new IndexPublisher(
            store, Repo, Lower, credential, sequence, highestIndexGenerationSeen: 41))
        {
            var (_, first) = await publisher.PublishDeltaDetailedAsync(
                0, [], [Entry(1, 1)], [], [], CancellationToken.None);
            var (_, second) = await publisher.PublishDeltaDetailedAsync(
                0, [], [Entry(2, 1)], [], [], CancellationToken.None);
            var skipped = sequence.AllocateNext();
            await publisher.PublishVoidDeltaAsync(0, skipped, CancellationToken.None);

            Assert.AreEqual(42UL, first.IndexGeneration);
            Assert.AreEqual(43UL, second.IndexGeneration);
        }

        // The void delta advanced it too: what the reader loads says so.
        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(0, IndexLoader.GapPatienceGenerations, null, null, CancellationToken.None);
        SequenceAssert.AreEqual(
            [42UL, 43UL, 44UL],
            state.Deltas.OrderBy(delta => delta.Delta.Sequence).Select(delta => delta.Delta.IndexGeneration ?? 0));
    }

    [TestMethod]
    public async Task AWritersOwnLastIndexGeneration_OutlivesItsProcess()
    {
        // A publication with no catalogue to ask, or one whose catalogue is
        // waiting to be rebuilt, still publishes above its own last delta.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();

        using (var publisher = new IndexPublisher(
            store, Repo, Lower, credential, Sequence("outlives"), highestIndexGenerationSeen: 41))
        {
            await publisher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
        }

        var reloaded = Sequence("outlives");
        Assert.AreEqual(42UL, reloaded.LastIndexGeneration);

        using var next = new IndexPublisher(store, Repo, Lower, credential, reloaded, highestIndexGenerationSeen: 0);
        var (_, delta) = await next.PublishDeltaDetailedAsync(0, [], [Entry(2, 1)], [], [], CancellationToken.None);
        Assert.AreEqual(43UL, delta.IndexGeneration);
    }

    [TestMethod]
    public async Task AWriterBelowFormatFour_PublishesNoIndexGeneration()
    {
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        using var publisher = new IndexPublisher(store, Repo, Lower, credential, Sequence("below"));

        var (_, delta) = await publisher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);

        Assert.IsNull(delta.IndexGeneration);
    }

    [TestMethod]
    public async Task TheLaterIndexGeneration_Wins_WhicheverWriterPublishedIt()
    {
        // The case ADR-0017 Amendment 1 found: at one generation the higher
        // writer id wins, whatever was published second. With an index
        // generation, the entry published second wins.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();

        using (var higher = new IndexPublisher(store, Repo, Higher, credential, Sequence("higher"), highestIndexGenerationSeen: 0))
        {
            await higher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
        }

        using (var lower = new IndexPublisher(store, Repo, Lower, credential, Sequence("lower"), highestIndexGenerationSeen: 1))
        {
            await lower.PublishDeltaDetailedAsync(0, [], [Entry(1, 2)], [], [], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(0, IndexLoader.GapPatienceGenerations, null, null, CancellationToken.None);

        var winner = Assert.ContainsSingle(state.Resolved).Value;
        Assert.AreEqual(Entry(1, 2).BlobId, winner.Entry.BlobId);
        Assert.AreEqual(2UL, winner.Generation);
    }

    [TestMethod]
    public async Task WithoutIndexGenerations_TheWriterIdStillDecides()
    {
        // Format-3 history ranks as it always did: every entry at zero, the
        // writer id's bytes deciding between two writers.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();

        using (var higher = new IndexPublisher(store, Repo, Higher, credential, Sequence("higher")))
        {
            await higher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
        }

        using (var lower = new IndexPublisher(store, Repo, Lower, credential, Sequence("lower")))
        {
            await lower.PublishDeltaDetailedAsync(0, [], [Entry(1, 2)], [], [], CancellationToken.None);
        }

        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(0, IndexLoader.GapPatienceGenerations, null, null, CancellationToken.None);

        Assert.AreEqual(Entry(1, 1).BlobId, Assert.ContainsSingle(state.Resolved).Value.Entry.BlobId);
    }

    [TestMethod]
    public async Task AGapAgesFromTheDeltaAfterIt_AndIsDamageOnceThePatienceHasPassed()
    {
        // A writer that is still publishing: under the rule as built only a
        // writer left behind was ever judged, so this gap was never reported.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = Sequence("ages");

        using var publisher = new IndexPublisher(store, Repo, Lower, credential, sequence, highestIndexGenerationSeen: 0);
        await publisher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
        var missing = sequence.AllocateNext();

        // The delta after the gap is at index generation 2. Seven more bring
        // the highest to 9: seven generations have passed, one short.
        for (var published = 0; published < 8; published++)
        {
            await publisher.PublishDeltaDetailedAsync(0, [], [Entry((byte)(2 + published), 1)], [], [], CancellationToken.None);
        }

        var patient = await LoadAsync(store, credential);
        SequenceAssert.AreEqual([(Lower, missing)], patient.UnresolvedGaps);
        Assert.IsEmpty(patient.Findings);

        await publisher.PublishDeltaDetailedAsync(0, [], [Entry(20, 1)], [], [], CancellationToken.None);

        var exhausted = await LoadAsync(store, credential);
        Assert.IsEmpty(exhausted.UnresolvedGaps);
        var finding = Assert.ContainsSingle(exhausted.Findings);
        Assert.AreEqual(DamageKind.MissingIndexObject, finding.Kind);
        Assert.Contains($"sequence {missing}", finding.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStoppedWritersGap_AgesAsAnotherWriterPublishes()
    {
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var stopped = Sequence("stopped");

        using (var publisher = new IndexPublisher(store, Repo, Higher, credential, stopped, highestIndexGenerationSeen: 0))
        {
            await publisher.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
            stopped.AllocateNext();
            await publisher.PublishDeltaDetailedAsync(0, [], [Entry(2, 1)], [], [], CancellationToken.None);
        }

        using (var active = new IndexPublisher(store, Repo, Lower, credential, Sequence("active"), highestIndexGenerationSeen: 2))
        {
            for (var published = 0; published < IndexLoader.GapPatienceGenerations; published++)
            {
                await active.PublishDeltaDetailedAsync(0, [], [Entry((byte)(10 + published), 2)], [], [], CancellationToken.None);
            }
        }

        var state = await LoadAsync(store, credential);
        Assert.AreEqual(DamageKind.MissingIndexObject, Assert.ContainsSingle(state.Findings).Kind);
    }

    [TestMethod]
    public async Task AGapBeforeADeltaWithoutAnIndexGeneration_IsNotAgedByThem()
    {
        // A gap in format-3 history has no index generation to age from. It
        // stays as it was before the upgrade, unresolved, however many
        // format-4 deltas follow it.
        var store = CreateStore();
        using var credential = TestAuthority.Shared.Credential.Clone();
        var sequence = Sequence("history");

        using (var before = new IndexPublisher(store, Repo, Lower, credential, sequence))
        {
            await before.PublishDeltaDetailedAsync(0, [], [Entry(1, 1)], [], [], CancellationToken.None);
            sequence.AllocateNext();
            await before.PublishDeltaDetailedAsync(0, [], [Entry(2, 1)], [], [], CancellationToken.None);
        }

        using (var after = new IndexPublisher(store, Repo, Lower, credential, sequence, highestIndexGenerationSeen: 0))
        {
            for (var published = 0; published < 2 * IndexLoader.GapPatienceGenerations; published++)
            {
                await after.PublishDeltaDetailedAsync(0, [], [Entry((byte)(10 + published), 1)], [], [], CancellationToken.None);
            }
        }

        var state = await LoadAsync(store, credential);
        Assert.ContainsSingle(state.UnresolvedGaps);
        Assert.IsEmpty(state.Findings);
    }

    private static async Task<IndexState> LoadAsync(LocalFileSystemObjectStore store, RepositoryWriteCredential credential)
    {
        using var loader = new IndexLoader(store, Repo, credential);
        return await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
