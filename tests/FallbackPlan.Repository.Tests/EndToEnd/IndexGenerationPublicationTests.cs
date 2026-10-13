using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What a backup stamps its deltas with (FR-MAN-015; specification 07 §2
/// key 12; ADR-0096). At format 4 every delta, void deltas included, carries
/// one more than the highest index generation the writer has seen: the
/// highest in the catalogue the publication applies its delta to, or the
/// writer's own last, whichever is higher. Below format 4 none does.
/// </summary>
[TestClass]
public sealed class IndexGenerationPublicationTests : ArchiveTestHarness
{
    private const int KiB = 1024;

    private const ulong Now = 1_722_600_000_000;

    private static readonly WriterId Other =
        WriterId.FromBytes(Enumerable.Repeat((byte)0xEE, 16).ToArray());

    [TestMethod]
    public async Task AFormatFourBackup_PublishesAboveTheHighestItsCatalogueHolds()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "above.db"), Repo);

        // Another writer's delta, rebuilt into this catalogue at index
        // generation 7: an adopted archive's history, say.
        catalogue.ApplyDelta(
            DeltaId.FromBytes(Enumerable.Repeat((byte)0xD1, 16).ToArray()),
            new IndexDelta { WriterId = Other, Sequence = 3, Generation = 0, IndexGeneration = 7 });

        await Orchestrator(store, keys, credential, Sequence("above"), FormatVersions.IndexGenerations, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 100), 0xA1, Now), CancellationToken.None);

        var published = await OwnDeltasAsync(store, credential);
        Assert.IsNotEmpty(published);
        Assert.AreEqual(8UL, published[0].IndexGeneration);
        SequenceAssert.AreEqual(
            Enumerable.Range(8, published.Count).Select(generation => (ulong?)(ulong)generation),
            published.Select(delta => delta.IndexGeneration));
    }

    [TestMethod]
    public async Task AFormatFourStreamBackup_PublishesAboveTheHighestItsCatalogueHolds()
    {
        // The single-stream path reads its catalogue on its own, so it is held
        // to the same rule separately.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "stream.db"), Repo);
        catalogue.ApplyDelta(
            DeltaId.FromBytes(Enumerable.Repeat((byte)0xD2, 16).ToArray()),
            new IndexDelta { WriterId = Other, Sequence = 3, Generation = 0, IndexGeneration = 11 });

        using var source = new MemoryStream(Incompressible(700, 64 * KiB));
        await Orchestrator(store, keys, credential, Sequence("stream"), FormatVersions.IndexGenerations, catalogue)
            .PublishAsync(Job(source, 0xE1), CancellationToken.None);

        Assert.AreEqual(12UL, Assert.ContainsSingle(await OwnDeltasAsync(store, credential)).IndexGeneration);
    }

    [TestMethod]
    public async Task AFormatFourBackup_WithNoCatalogue_PublishesAboveItsOwnLastDelta()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var sequence = Sequence("own");

        for (var run = 0; run < 2; run++)
        {
            using var source = new MemoryStream(Incompressible(500 + run, 64 * KiB));
            await Orchestrator(store, keys, credential, sequence, FormatVersions.IndexGenerations, catalogue: null)
                .PublishAsync(Job(source, (byte)(0xB1 + run)), CancellationToken.None);
        }

        var published = await OwnDeltasAsync(store, credential);
        Assert.HasCount(2, published);
        SequenceAssert.AreEqual([(ulong?)1UL, 2UL], published.Select(delta => delta.IndexGeneration));
    }

    [TestMethod]
    public async Task AFormatFourBackup_VoidsALeftoverAtAnIndexGenerationToo()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var sequence = Sequence("void");

        // A number a dead run allocated and never used.
        var leftover = sequence.AllocateNext();

        using (var source = new MemoryStream(Incompressible(600, 64 * KiB)))
        {
            await Orchestrator(store, keys, credential, sequence, FormatVersions.IndexGenerations, catalogue: null)
                .PublishAsync(Job(source, 0xC1), CancellationToken.None);
        }

        var voided = Assert.ContainsSingle((await OwnDeltasAsync(store, credential)).Where(delta => delta.IsVoid));
        Assert.AreEqual(leftover, voided.Sequence);
        Assert.IsNotNull(voided.IndexGeneration);
    }

    [TestMethod]
    public async Task AFormatThreeBackup_PublishesNoIndexGeneration()
    {
        // Key 12 is refused by every reader that predates format 4, so a
        // repository such a reader is entitled to read must never hold one.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "three.db"), Repo);

        await Orchestrator(store, keys, credential, Sequence("three"), FormatVersions.RelocatableRecords, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 200), 0xD1, Now), CancellationToken.None);

        var published = await OwnDeltasAsync(store, credential);
        Assert.IsNotEmpty(published);
        Assert.IsTrue(published.All(delta => delta.IndexGeneration is null));
    }

    private static async Task<List<IndexDelta>> OwnDeltasAsync(
        LocalFileSystemObjectStore store, RepositoryWriteCredential credential)
    {
        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);
        return [.. state.Deltas
            .Select(decoded => decoded.Delta)
            .Where(delta => delta.WriterId == Writer)
            .OrderBy(delta => delta.Sequence)];
    }

    private static byte[] Incompressible(int seed, int length)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static FakeFileSystemSource Tree(int files, int seed)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"data/{index:d3}.bin", Incompressible(seed + index, 256 * KiB), fileId: (ulong)(40_000 + index));
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static BackupJob Job(Stream source, byte snapshotSeed) => new(
        source,
        "report.bin"u8.ToArray(),
        Enumerable.Repeat((byte)0x22, 16).ToArray(),
        Enumerable.Repeat((byte)0x33, 16).ToArray(),
        Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        Now, 3_600_000, 5, "tests/1.0");

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"{name}-sequence.txt")));

    private PublicationOrchestrator Orchestrator(
        IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        ushort formatVersion,
        CatalogueDb? catalogue) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            sequence,
            SpoolDirectory,
            formatVersion,
            observer: null,
            catalogue);
}
