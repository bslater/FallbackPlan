using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// How many files the next backup will take, counted over only the folders a
/// change journal named since the last (ADR-0192, FR-SNP-013): a dry walk
/// that enters a named folder, the folders above it, and a folder the last
/// snapshot does not hold, and passes over every other folder as the backup
/// will. A file new, changed, changed only in its metadata, or moved there is
/// counted, judged as the backup judges it and under the set's rules; one
/// unchanged is not, and nothing is opened.
/// </summary>
/// <remarks>
/// The source is a stand-in, so this runs on every platform.
/// </remarks>
[TestClass]
public sealed class ChangedFilesCountTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task AFileChangedInANamedFolder_IsCounted_AndAFolderNotNamedIsNotEntered()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue);

        Change(tree.A, 9);
        Change(tree.B, 9);
        tree.Source.ListedFolders.Clear();
        var count = await CountAsync(tree.Source, catalogue, baseline, ["docs"]);

        Assert.AreEqual(new ChangedFileCount(New: 0, Updated: 1, MetadataOnly: 0, Moved: 0), count);
        Assert.AreEqual(1, count.Total);
        Assert.DoesNotContain("photos", tree.Source.ListedFolders, "a folder nothing named is passed over, as the backup will");
        Assert.IsEmpty(tree.Source.OpenedPaths, "a count opens nothing");
    }

    [TestMethod]
    public async Task AFileNew_AndAFolderTheLastSnapshotDoesNotHold_AreCountedWhole()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue);

        tree.Source.AddFile("docs/new.txt", Bytes(100, 3));
        tree.Source.AddFile("docs/sub/x.txt", Bytes(100, 4));
        tree.Source.AddFile("docs/sub/deeper/y.txt", Bytes(100, 5));
        var count = await CountAsync(tree.Source, catalogue, baseline, ["docs"]);

        Assert.AreEqual(3, count.New, "the folder made since is walked whole, as the backup will walk it");
        Assert.AreEqual(3, count.Total);
    }

    [TestMethod]
    public async Task AFileChangedInItsMetadataOnly_AndAFileMoved_AreCounted()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue);

        tree.A.Metadata = tree.A.Metadata with { PosixMode = 0x180 };
        tree.Source.Remove("photos/b.jpg");
        tree.Source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "docs/b.jpg",
            Content = tree.B.Content,
            Identity = tree.B.Identity,
            Metadata = tree.B.Metadata,
        });
        var count = await CountAsync(tree.Source, catalogue, baseline, ["docs", "photos"]);

        Assert.AreEqual(new ChangedFileCount(New: 0, Updated: 0, MetadataOnly: 1, Moved: 1), count);
    }

    [TestMethod]
    public async Task NothingNamed_CountsNothing_AndEntersNoFolderBelowTheRoot()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue);

        tree.Source.ListedFolders.Clear();
        var count = await CountAsync(tree.Source, catalogue, baseline, []);

        Assert.AreEqual(0, count.Total);
        Assert.DoesNotContain("docs", tree.Source.ListedFolders);
        Assert.DoesNotContain("photos", tree.Source.ListedFolders);
    }

    [TestMethod]
    public async Task AFileUnchangedInANamedFolder_IsNotCounted()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue);

        var count = await CountAsync(tree.Source, catalogue, baseline, ["docs"]);

        Assert.AreEqual(0, count.Total);
    }

    [TestMethod]
    public async Task AFileTheRulesLeaveOut_IsNotCounted()
    {
        var tree = Tree();
        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(tree.Source, catalogue, excludes: ["**/*.tmp"]);

        tree.Source.AddFile("docs/scratch.tmp", Bytes(100, 6));
        tree.Source.AddFile("docs/kept.txt", Bytes(100, 7));
        var count = await CountAsync(tree.Source, catalogue, baseline, ["docs"], excludes: ["**/*.tmp"]);

        Assert.AreEqual(1, count.New);
    }

    private static Fixture Tree()
    {
        var source = new FakeFileSystemSource();
        var a = source.AddFile("docs/a.txt", Bytes(400, 1));
        var b = source.AddFile("photos/b.jpg", Bytes(400, 2));
        source.AddFile("top.txt", Bytes(100, 8));
        return new Fixture(source, a, b);
    }

    private static void Change(FakeFileSystemSource.Node node, byte seed)
    {
        node.Content = Bytes(node.Content.Length + 10, seed);
        node.Metadata = node.Metadata with { ModifiedAt = node.Metadata.ModifiedAt + 60_000 };
    }

    private static ValueTask<ChangedFileCount> CountAsync(
        FakeFileSystemSource source,
        CatalogueDb catalogue,
        ReadOnlyMemory<byte> baseline,
        IReadOnlyCollection<string> changed,
        IReadOnlyList<string>? excludes = null) =>
        SourceComparer.CountChangedAsync(
            source, [new ScanRoot("/")], [], excludes ?? [], catalogue, baseline, changed, CancellationToken.None);

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private async Task<ReadOnlyMemory<byte>> PublishBaselineAsync(
        FakeFileSystemSource source, CatalogueDb catalogue, IReadOnlyList<string>? excludes = null)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        await new PublicationOrchestrator(
                SmallBlobPolicy,
                Repo,
                Writer,
                KeyGeneration.Zero,
                keys,
                credential,
                store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory,
                FormatVersions.SealedDataPlane,
                observer: null,
                catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    ExcludeRules = excludes ?? [],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "fallbackplan-tests/1.0",
                },
                CancellationToken.None);
        source.OpenedPaths.Clear();
        return Assert.ContainsSingle(catalogue.EnumerateSnapshots()).SnapshotId;
    }

    private static byte[] Bytes(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>The stand-in source and the two files a test changes.</summary>
    private sealed record Fixture(FakeFileSystemSource Source, FakeFileSystemSource.Node A, FakeFileSystemSource.Node B);
}
