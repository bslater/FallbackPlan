using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Both ends of the range: a snapshot with nothing in it, and a directory
/// with far more in it than any query was written for (FR-SNP-002, FR-MAN-002,
/// NFR-PERF-004).
/// </summary>
/// <remarks>
/// <para>
/// Two shipped fixes from the surveyed changelog sit at these ends. Empty
/// snapshots being created is the zero end — a backup that captured nothing
/// still wrote a version, and versions that describe nothing accumulate and
/// confuse restore. A query needing more than 128 parameters is the many end:
/// a query built by pasting one placeholder per item works beautifully until
/// the item count crosses a limit nobody remembered was there.
/// </para>
/// <para>
/// FallbackPlan has no dynamic parameter lists — every catalogue query takes
/// a fixed set and the variable-length work happens in the tree-manifest chain
/// (06 §9) — so the specific SQLite limit cannot be hit. That is worth a test
/// rather than a claim, because the property being relied on is "nobody
/// introduces one", and a test at a few thousand entries is what notices when
/// somebody does.
/// </para>
/// </remarks>
[TestClass]
public sealed class SnapshotScaleEdgeTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    /// <summary>
    /// Comfortably past SQLite's 999-parameter default and past the 128 the
    /// surveyed note names, while still running in a second or two.
    /// </summary>
    private const int Many = 2_500;

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
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

    /// <summary>
    /// A backup of a source with nothing in it publishes a snapshot that says
    /// so, and does not fail.
    /// </summary>
    /// <remarks>
    /// Publishing the empty snapshot is the right behaviour and is not the
    /// thing the surveyed product fixed: "there were no files on this date" is a true and
    /// useful statement, and suppressing it would leave a gap in the history
    /// indistinguishable from the agent not having run. What must not happen
    /// is a crash, or a snapshot that claims content it does not have.
    /// </remarks>
    [TestMethod]
    public async Task ASourceWithNoFilesAtAll_PublishesAnHonestlyEmptySnapshot()
    {
        var source = new FakeFileSystemSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.IsEmpty(published.Files);
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual(0, published.ContentBlobs.Sum(blob => blob.RecordCount));

        // And it is a real snapshot the catalogue knows about, not a silent
        // no-op: a history with a gap in it cannot be told from an agent that
        // did not run.
        Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        Assert.IsEmpty(catalogue.ListDirectory(Enumerable.Repeat((byte)0xA1, 16).ToArray(), string.Empty));
    }

    /// <summary>
    /// A second empty backup is still a second snapshot, and neither one
    /// disturbs the other.
    /// </summary>
    [TestMethod]
    public async Task TwoEmptyBackupsInARow_ProduceTwoIndependentSnapshots()
    {
        var source = new FakeFileSystemSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);
        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                },
                CancellationToken.None);

        Assert.AreEqual(2, catalogue.EnumerateSnapshots().Count);
    }

    /// <summary>
    /// A file that vanishes so that the set becomes empty is a deletion, not a
    /// failure — the earlier snapshot keeps the file and the newer one does
    /// not have it.
    /// </summary>
    [TestMethod]
    public async Task TheLastFileIsDeleted_TheEarlierSnapshotKeepsIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("only.txt", [.. Enumerable.Range(0, 400).Select(value => (byte)value)]);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.IsTrue(source.Remove("only.txt"));

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.IsEmpty(second.Files);
        Assert.IsEmpty(second.Failures);
        Assert.ContainsSingle(catalogue.ListDirectory(Enumerable.Repeat((byte)0xA1, 16).ToArray(), string.Empty));
        Assert.IsEmpty(catalogue.ListDirectory(Enumerable.Repeat((byte)0xB2, 16).ToArray(), string.Empty));
    }

    /// <summary>
    /// The many end: one directory holding thousands of entries publishes,
    /// lists and looks up correctly. A query built one placeholder per item
    /// would fail somewhere in here.
    /// </summary>
    [TestMethod]
    public async Task ADirectoryOfThousandsOfFiles_PublishesAndListsCompletely()
    {
        var source = new FakeFileSystemSource();
        for (var i = 0; i < Many; i++)
        {
            source.AddFile($"bulk/file-{i:D5}.bin", [(byte)i, (byte)(i >> 8), 0x5A]);
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.AreEqual(Many, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        var snapshot = Enumerable.Repeat((byte)0xA1, 16).ToArray();
        var listed = catalogue.ListDirectory(snapshot, "bulk");
        Assert.AreEqual(Many, listed.Count);

        // Listing order is path order, so the whole set is present rather than
        // a page of it.
        Assert.AreEqual("bulk/file-00000.bin", listed[0].Path);
        Assert.AreEqual($"bulk/file-{Many - 1:D5}.bin", listed[^1].Path);

        // And a targeted lookup resolves anywhere in the range.
        foreach (var index in new[] { 0, 127, 128, 129, 998, 999, 1000, Many - 1 })
        {
            Assert.IsNotNull(
                catalogue.LookupPath(snapshot, $"bulk/file-{index:D5}.bin"),
                $"file-{index:D5}.bin did not resolve");
        }
    }

    /// <summary>
    /// And the incremental pass over that many entries reuses all of them,
    /// which is the path that reads the catalogue once per file.
    /// </summary>
    [TestMethod]
    public async Task ASecondPassOverThousandsOfUnchangedFiles_ReusesEveryOne()
    {
        var source = new FakeFileSystemSource();
        for (var i = 0; i < Many; i++)
        {
            source.AddFile($"bulk/file-{i:D5}.bin", [(byte)i, (byte)(i >> 8), 0x5A]);
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        source.OpenedPaths.Clear();
        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        Assert.AreEqual(Many, second.Files.Count);
        Assert.AreEqual(Many, second.Files.Count(file => file.Reused));
        Assert.IsEmpty(source.OpenedPaths, "an unchanged file was re-read at scale");
    }
}
