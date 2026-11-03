using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A run told which folders changed since its prior snapshot walks only those
/// and the folders above them, and takes every other folder as the prior
/// snapshot held it, its tree reused without being listed (ADR-0186,
/// FR-SNP-010). Told truly, it makes the snapshot a full walk would. A folder
/// holding a file the prior snapshot failed to capture is walked, so the file
/// is tried again, as is a folder the prior snapshot does not hold. Without a
/// prior snapshot, or where it was captured under other rules or other
/// folders, every folder is walked. A reused folder's trees are part of the
/// new snapshot, so damage to them reaches it, and its rows stay in the
/// catalogue though the prior snapshot is forgotten during the run. What the
/// run is not told waits for the next walk.
/// </summary>
[TestClass]
public sealed class FolderReuseTests : ArchiveTestHarness
{
    private static readonly byte[] First = Enumerable.Repeat((byte)0xA1, 16).ToArray();
    private static readonly byte[] Second = Enumerable.Repeat((byte)0xB2, 16).ToArray();

    [TestMethod]
    public async Task AFolderNotNamedChanged_IsReusedWithoutBeingWalked()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        source.ListedFolders.Clear();
        source.OpenedPaths.Clear();

        source.AddFile("photos/beach.jpg", Deterministic(3_000, 9));
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: ["photos"]), CancellationToken.None);

        // The root and the folder named changed; nothing in docs, whose
        // tree is the one the first snapshot recorded.
        Assert.DoesNotContain("docs", source.ListedFolders);
        Assert.DoesNotContain("docs/sub", source.ListedFolders);
        Assert.Contains("photos", source.ListedFolders);
        SequenceAssert.AreEqual(["photos/beach.jpg"], source.OpenedPaths.Distinct());
        Assert.AreEqual(1, second.FoldersReused, "docs alone: what is below it was never asked");

        Assert.AreEqual(
            catalogue.LookupDirectory(First, "docs")!.TreeId,
            catalogue.LookupDirectory(Second, "docs")!.TreeId);
        Assert.IsNotNull(catalogue.LookupPath(Second, "docs/sub/deep.txt"), "a reused folder's files are the new snapshot's");
    }

    [TestMethod]
    public async Task ARunToldTruly_MakesTheSnapshotAFullWalkWould()
    {
        var hinted = await TwoRunsAsync("hinted", changed: ["photos"]);
        var walked = await TwoRunsAsync("walked", changed: null);

        Assert.AreEqual(walked, hinted, "the same root tree, whichever way the run went");
    }

    [TestMethod]
    public async Task AChangeTheRunWasNotTold_WaitsForTheNextWalk()
    {
        // What the run is told it trusts until a walk: that is the price of
        // not walking, and why a full walk stays on its schedule.
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        var before = catalogue.LookupPath(First, "docs/a.txt")!.ObjectId;

        source.AddFile("docs/a.txt", Deterministic(700, 42));
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: ["photos"]), CancellationToken.None);
        Assert.AreEqual(before, catalogue.LookupPath(Second, "docs/a.txt")!.ObjectId);

        var third = Enumerable.Repeat((byte)0xC3, 16).ToArray();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Job(source, third, now: 1_722_800_000_001) with { PriorSnapshotId = Second, ParentSnapshots = [Second] },
            CancellationToken.None);
        Assert.AreNotEqual(before, catalogue.LookupPath(third, "docs/a.txt")!.ObjectId);
    }

    [TestMethod]
    public async Task AFolderHoldingAFileThePriorSnapshotFailed_IsWalked()
    {
        var source = Tree();
        var locked = source.AddFile("docs/sub/locked.bin", Deterministic(500, 5));
        locked.OpenFailure = new IOException("the file is locked");
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        source.ListedFolders.Clear();

        locked.OpenFailure = null;
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: []), CancellationToken.None);

        // Nothing changed, but the file that failed is tried again, and only
        // the folders on its way are walked.
        Assert.Contains("docs", source.ListedFolders);
        Assert.Contains("docs/sub", source.ListedFolders);
        Assert.DoesNotContain("photos", source.ListedFolders);
        Assert.IsEmpty(second.Failures);
        Assert.IsNotNull(catalogue.LookupPath(Second, "docs/sub/locked.bin"));
    }

    [TestMethod]
    public async Task AFolderThePriorSnapshotDoesNotHold_IsWalked()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        source.ListedFolders.Clear();

        // A new folder changes the folder that holds it, which is all a
        // change source can say of it.
        source.AddFile("new/fresh.txt", Deterministic(300, 6));
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: [""]), CancellationToken.None);

        Assert.Contains("new", source.ListedFolders);
        Assert.IsNotNull(catalogue.LookupPath(Second, "new/fresh.txt"));
    }

    [TestMethod]
    public async Task WithoutAPriorSnapshot_EveryFolderIsWalked()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Job(source, First) with { ChangedFolders = [] }, CancellationToken.None);

        Assert.Contains("docs/sub", source.ListedFolders);
        Assert.AreEqual(0, published.FoldersReused);
        Assert.IsNotNull(catalogue.LookupPath(First, "docs/sub/deep.txt"));
    }

    [TestMethod]
    public async Task AReusedFolder_IsPartOfTheNewSnapshotsMakeUp()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        source.AddFile("photos/beach.jpg", Deterministic(3_000, 9));

        // Renamed, so the second snapshot's configuration is an object of its
        // own and only docs' trees tie it to the first's blobs. A set's name
        // is not what a walk finds, so the folder is still reused.
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: ["photos"]) with { SetName = "renamed" }, CancellationToken.None);
        Assert.AreEqual(1, second.FoldersReused);

        // The first snapshot's metadata holds docs' trees, which the second
        // reused: damage to them reaches both as part of what each is made of.
        var reach = catalogue.ReachOf([.. first.MetadataBlobs.Select(blob => blob.BlobId)], sampleLimit: 0);
        Assert.AreEqual(2, reach.Structures);
        Assert.IsTrue(reach.Complete);
    }

    [TestMethod]
    public async Task InASetOfSeveralFolders_AFolderUnderALabelIsReused()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("alpha/docs/a.txt", Deterministic(400, 1));
        source.AddFile("bravo/pics/beach.jpg", Deterministic(900, 2));
        ScanRoot[] roots = [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")];
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Job(source, First) with { Roots = roots }, CancellationToken.None);
        source.ListedFolders.Clear();

        source.AddFile("bravo/pics/beach.jpg", Deterministic(901, 3));
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: ["Photos/pics"]) with { Roots = roots }, CancellationToken.None);

        Assert.DoesNotContain("alpha/docs", source.ListedFolders);
        Assert.AreEqual(1, second.FoldersReused);
        Assert.IsNotNull(catalogue.LookupPath(Second, "Documents/docs/a.txt"));
    }

    [TestMethod]
    public async Task AReusedFolder_StaysInTheCatalogue_ThoughThePriorSnapshotIsForgottenMidRun()
    {
        var source = Tree();
        source.Folders["docs/sub"] = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1ED, AccessedAt = 1_722_000_000_500 };
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);

        // The prior snapshot is forgotten on another connection once the run
        // has decided what to reuse and before its own row lands, which lets
        // go of every folder only that snapshot reached (ADR-0151 §4).
        var beach = source.AddFile("photos/beach.jpg", Deterministic(3_000, 9));
        beach.OnOpened = _ =>
        {
            using var other = OpenCatalogue();
            other.ForgetSnapshot(First);
        };
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: ["photos"]), CancellationToken.None);

        Assert.AreEqual(1, second.FoldersReused);
        Assert.IsEmpty(catalogue.EnumerateSnapshots().Where(row => row.SnapshotId.Span.SequenceEqual(First)));
        Assert.IsNotNull(catalogue.LookupPath(Second, "docs/a.txt"));
        Assert.IsNotNull(catalogue.LookupPath(Second, "docs/sub/deep.txt"), "a reused folder's rows are recorded again");
        Assert.AreEqual(1_722_000_000_500UL, catalogue.LookupDirectory(Second, "docs/sub")!.AccessedAt, "and the access times its trees state");
    }

    [TestMethod]
    public async Task ARunWhoseRulesChanged_WalksEveryFolder()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        source.ListedFolders.Clear();

        // A change source sees the files, not the set's rules: a folder the
        // new rules cut into is one the prior snapshot holds under the old.
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: []) with { ExcludeRules = ["docs/sub/**"] }, CancellationToken.None);

        Assert.Contains("docs", source.ListedFolders);
        Assert.Contains("photos", source.ListedFolders);
        Assert.AreEqual(0, second.FoldersReused);
        Assert.IsNull(catalogue.LookupPath(Second, "docs/sub/deep.txt"));
        Assert.IsNotNull(catalogue.LookupPath(Second, "docs/a.txt"));
    }

    [TestMethod]
    public async Task ARunWhoseFoldersChanged_WalksEveryFolder()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("alpha/docs/a.txt", Deterministic(400, 1));
        source.AddFile("bravo/docs/b.txt", Deterministic(900, 2));
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Job(source, First) with { Roots = [new ScanRoot("alpha", "Documents"), new ScanRoot("bravo", "Photos")] },
            CancellationToken.None);
        source.ListedFolders.Clear();

        // The labels now name each other's folder: Documents/docs is
        // bravo's, which the prior snapshot never held there.
        var second = await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(
            Next(source, changed: []) with { Roots = [new ScanRoot("bravo", "Documents"), new ScanRoot("alpha", "Photos")] },
            CancellationToken.None);

        Assert.AreEqual(0, second.FoldersReused);
        Assert.IsNotNull(catalogue.LookupPath(Second, "Documents/docs/b.txt"));
        Assert.IsNotNull(catalogue.LookupPath(Second, "Photos/docs/a.txt"));
    }

    [TestMethod]
    public async Task ReadSubtree_ReadsEveryFolderBelowWhole_AndNoneWhereOneWouldSplit()
    {
        var source = Tree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await CreateOrchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);

        var docs = catalogue.LookupDirectory(First, "docs")!.TreeId;
        var sub = catalogue.LookupDirectory(First, "docs/sub")!.TreeId;

        var subtree = catalogue.ReadSubtree(First, "docs", TreeChainWriter.DefaultShardBudget);
        Assert.IsNotNull(subtree);
        Assert.AreEqual(docs, subtree.TreeId);
        CollectionAssert.AreEquivalent(new[] { docs, sub }, subtree.Folders.Select(folder => folder.TreeId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "a.txt", "sub" },
            subtree.Folders.Single(folder => folder.TreeId == docs).Entries.Select(entry => entry.Name).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "deep.txt" },
            subtree.Folders.Single(folder => folder.TreeId == sub).Entries.Select(entry => entry.Name).ToArray());
        Assert.IsNull(catalogue.ReadSubtree(First, "nowhere", TreeChainWriter.DefaultShardBudget));
        Assert.IsNull(catalogue.ReadSubtree(Second, "docs", TreeChainWriter.DefaultShardBudget), "a snapshot the catalogue does not hold");

        // A folder's tree splits once its names' estimate passes the budget:
        // docs holds a.txt and sub, 5 + 48 and 3 + 48 bytes, over 100.
        Assert.IsNull(catalogue.ReadSubtree(First, "docs", shardBudget: 100));
        Assert.IsNotNull(catalogue.ReadSubtree(First, "docs/sub", shardBudget: 100), "deep.txt alone, 8 + 48 bytes, is not");
    }

    private async Task<ObjectId> TwoRunsAsync(string name, IReadOnlyCollection<string>? changed)
    {
        var source = Tree();
        var store = new Storage.Local.LocalFileSystemObjectStore(Path.Combine(StoreRoot, name));
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, name + ".db"), Repo);

        await CreateOrchestrator(store, keys, credential, catalogue, name).PublishAsync(Job(source, First), CancellationToken.None);
        source.AddFile("photos/beach.jpg", Deterministic(3_000, 9));
        var second = await CreateOrchestrator(store, keys, credential, catalogue, name).PublishAsync(
            Next(source, changed), CancellationToken.None);
        return second.RootTreeObjectId;
    }

    private static FakeFileSystemSource Tree()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", Deterministic(700, 1));
        source.AddFile("docs/sub/deep.txt", Deterministic(800, 2));
        source.AddFile("photos/beach.jpg", Deterministic(2_000, 3));
        return source;
    }

    private static SnapshotJob Next(FakeFileSystemSource source, IReadOnlyCollection<string>? changed) =>
        Job(source, Second, now: 1_722_700_000_001) with
        {
            PriorSnapshotId = First,
            ParentSnapshots = [First],
            ChangedFolders = changed,
        };

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue, string sequence = "sequence") =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, sequence + ".txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte[] snapshotId, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = snapshotId,
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }
}
