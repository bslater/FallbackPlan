using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Multi-root publication (ADR-0040, FR-SNP-008): several folders capture
/// into one snapshot under a synthetic root whose top-level entries are the
/// roots, each named by its label — labels in raw-byte order because the
/// tree codec's MUST enforces it, rules speaking label-prefixed subjects,
/// and a single-root job keeping the pre-multi-root shape exactly.
/// </summary>
[TestClass]
public sealed class MultiRootPublicationTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue, string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);
    }

    private static SnapshotJob Job(
        FakeFileSystemSource source, IReadOnlyList<ScanRoot> roots, byte seed,
        IReadOnlyList<string>? includes = null, IReadOnlyList<string>? excludes = null) => new()
    {
        Source = source,
        Roots = roots,
        IncludeRules = includes ?? [],
        ExcludeRules = excludes ?? [],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Bytes(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>Two subtrees of the fake, playing two folders of a machine.</summary>
    private static FakeFileSystemSource TwoRootSource()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("alpha/notes.txt", Bytes(400, 1));
        source.AddFile("alpha/docs/report.txt", Bytes(600, 2));
        source.AddFile("bravo/pics/beach.jpg", Bytes(900, 3));
        return source;
    }

    [TestMethod]
    public async Task MultiRoot_TwoFolders_PublishOneSnapshotWithLabelledTopLevel()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("two-roots");

        // Deliberately given out of byte order — the publication must sort
        // by raw label bytes, or the tree codec's MUST throws at encode.
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "two-roots").PublishAsync(
            Job(source, [new ScanRoot("bravo", "Photos"), new ScanRoot("alpha", "Documents")], 0xA1),
            CancellationToken.None);

        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());

        // The synthetic root's entries are the labels, in ascending raw-byte
        // order, each a real directory tree beneath.
        var top = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);
        SequenceAssert.AreEqual(["Documents", "Photos"], top.Select(entry => entry.Path));
        Assert.IsTrue(top.All(entry => entry.EntryKind == EntryKind.DirectoryPlaceholder));

        // Every path beneath speaks the label coordinate system.
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "Documents/notes.txt"));
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "Documents/docs/report.txt"));
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "Photos/pics/beach.jpg"));
        Assert.IsNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "notes.txt"),
            "nothing lives at the old unprefixed coordinates");
    }

    [TestMethod]
    public async Task MultiRoot_RulesSpeakLabelPrefixedSubjects_AndPruneTheWalk()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("ruled");

        await CreateOrchestrator(store, keys, hierarchy, catalogue, "ruled").PublishAsync(
            Job(
                source,
                [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")],
                0xB2,
                excludes: ["Photos/pics"]),
            CancellationToken.None);

        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        Assert.IsNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "Photos/pics/beach.jpg"));
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "Documents/notes.txt"));

        // Pruned at the walk, not merely at capture: the excluded subtree's
        // content was never opened (the SubjectPrefix reached the scanner).
        Assert.IsFalse(source.OpenedPaths.Contains("bravo/pics/beach.jpg"),
            "an excluded subtree must not be read");
    }

    [TestMethod]
    public async Task MultiRoot_IncludeRulesLeaveARootEmpty_ItFoldsAway()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("folded");

        await CreateOrchestrator(store, keys, hierarchy, catalogue, "folded").PublishAsync(
            Job(
                source,
                [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")],
                0xC3,
                includes: ["Documents/**"]),
            CancellationToken.None);

        // A root that captured nothing and is not itself captured leaves no
        // entry — the tree records what the snapshot holds (ADR-0037 §3's
        // fold, unchanged under labels).
        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        var top = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);
        Assert.AreEqual("Documents", Assert.ContainsSingle(top).Path);
    }

    [TestMethod]
    public async Task MultiRoot_MissingOrDuplicateLabels_AreRefusedBeforeAnyByte()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("refused");
        var orchestrator = CreateOrchestrator(store, keys, hierarchy, catalogue, "refused");

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await orchestrator.PublishAsync(
            Job(source, [new ScanRoot("alpha", "Docs"), new ScanRoot("bravo")], 0xD4),
            CancellationToken.None));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await orchestrator.PublishAsync(
            Job(source, [new ScanRoot("alpha", "Docs"), new ScanRoot("bravo", "Docs")], 0xD5),
            CancellationToken.None));

        Assert.IsEmpty(catalogue.EnumerateSnapshots());
    }

    [TestMethod]
    public async Task SingleRoot_KeepsThePreMultiRootShape()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("single");

        // One root, label present but ignored: the tree's root is the folder
        // itself — no synthetic frame, no prefix, exactly the old coordinates.
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "single").PublishAsync(
            Job(source, [new ScanRoot("alpha", "IgnoredLabel")], 0xE5),
            CancellationToken.None);

        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        var top = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);
        SequenceAssert.AreEqual(["docs", "notes.txt"], top.Select(entry => entry.Path));
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "notes.txt"));
    }

    [TestMethod]
    public async Task MultiRoot_Restored_LaysOutOneFolderPerLabel()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("restored");

        await CreateOrchestrator(store, keys, hierarchy, catalogue, "restored").PublishAsync(
            Job(source, [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")], 0xF6),
            CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat((byte)0xF6, 16).ToArray(), string.Empty, target);

        var output = Path.Combine(SpoolDirectory, "restored-out");
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "multi-root-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        CollectionAssert.AreEqual(
            Bytes(400, 1), File.ReadAllBytes(Path.Combine(output, "Documents", "notes.txt")));
        CollectionAssert.AreEqual(
            Bytes(900, 3), File.ReadAllBytes(Path.Combine(output, "Photos", "pics", "beach.jpg")));
    }

    [TestMethod]
    public async Task SourceComparer_MultiRoot_ClassifiesInLabelCoordinates()
    {
        var source = TwoRootSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("compared");
        IReadOnlyList<ScanRoot> roots = [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")];

        await CreateOrchestrator(store, keys, hierarchy, catalogue, "compared")
            .PublishAsync(Job(source, roots, 0xA7), CancellationToken.None);
        var baseline = Assert.ContainsSingle(catalogue.EnumerateSnapshots()).SnapshotId;

        source.AddFile("alpha/added.txt", Bytes(100, 9));
        Assert.IsTrue(source.Remove("bravo/pics/beach.jpg"));

        var comparison = await SourceComparer.CompareAsync(
            source, roots, [], [], catalogue, baseline, sampleLimit: 20, CancellationToken.None);

        Assert.AreEqual("Documents/added.txt", Assert.ContainsSingle(comparison.New.Sample));
        Assert.AreEqual("Photos/pics/beach.jpg", Assert.ContainsSingle(comparison.Deleted.Sample));
        Assert.AreEqual(2, comparison.Unchanged);
    }

    [TestMethod]
    public void Intersection_SeveralProbes_KeepsOnlyWhatHoldsEverywhere()
    {
        var sensitiveSparse = new SourceFilesystemInfo(true, true, "ext4", 4096, 255, false);
        var insensitive = new SourceFilesystemInfo(false, true, "ntfs", 65534, 510, true);
        var noSparse = new SourceFilesystemInfo(true, false, "ext4", 4096, null, false);

        var merged = SourceFilesystemIntersection.Intersect([sensitiveSparse, insensitive, noSparse]);

        Assert.IsFalse(merged.CaseSensitive, "any case-insensitive root makes case a hazard everywhere");
        Assert.IsFalse(merged.SupportsSparse);
        Assert.AreEqual("ext4+ntfs", merged.Name);
        Assert.AreEqual(4096u, merged.MaxPathBytes);
        Assert.AreEqual(255u, merged.MaxComponentBytes);
        Assert.AreEqual(true, merged.ReservedNames);

        var alone = SourceFilesystemIntersection.Intersect([sensitiveSparse]);
        Assert.AreSame(sensitiveSparse, alone);
    }
}
