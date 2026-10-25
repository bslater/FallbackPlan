using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A folder whose only change is its access time keeps the tree it had
/// (NFR-PERF-003): reading a folder moves its access time wherever reads do,
/// and a backup reads every folder it walks. A tree that took that as a change
/// would be written anew for every folder, and every folder above it, on every
/// backup, though nothing in the tree changed.
/// </summary>
/// <remarks>
/// <para>
/// A file has had this rule since its metadata digest left access time out
/// (architecture 06 §4.3). A folder states its access time in its tree, and the
/// tree's identity is its bytes, so the same rule has to be met differently: a
/// folder whose tree would be the prior one but for its access time is given
/// the prior access time, and so is the prior tree. A folder with anything else
/// changed gets a new tree that states the access time it has now, as a new
/// file version does.
/// </para>
/// <para>
/// What the next backup compares against comes from the catalogue, which a
/// rebuild recreates from the repository, so the rule has to hold after either
/// rebuild as it does over a live catalogue. Those tests back up folders
/// alone: a rebuilt catalogue knows no file's identity, so the next backup
/// reads every file again and gives it a version naming the last as its
/// parent, which changes the file's folder whatever the folder's own rule.
/// </para>
/// </remarks>
[TestClass]
public sealed class FolderAccessTimeTests : ArchiveTestHarness
{
    private const ulong FirstRead = 1_722_000_000_000;

    private const ulong SecondRead = 1_722_600_000_000;

    private static readonly byte[] First = [.. Enumerable.Repeat((byte)0xA1, 16)];

    private static readonly byte[] Second = [.. Enumerable.Repeat((byte)0xB2, 16)];

    private CatalogueDb OpenCatalogue(string name = "catalogue.db") =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, name), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane, observer: null, catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte[] snapshotId, ulong now) => new()
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

    private static SnapshotJob After(FakeFileSystemSource source) =>
        Job(source, Second, now: 1_722_700_000_001) with
        {
            PriorSnapshotId = First,
            ParentSnapshots = [First],
        };

    private static FakeFileSystemSource Source()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/report.txt", [.. Enumerable.Range(0, 4_000).Select(value => (byte)(value * 7))]);
        source.Folders[string.Empty] = new EntryMetadata { ModifiedAt = 1_721_000_000_000, AccessedAt = FirstRead };
        source.Folders["docs"] = new EntryMetadata
        {
            ModifiedAt = 1_721_000_000_000, PosixMode = 0x1ED, AccessedAt = FirstRead,
        };
        return source;
    }

    private static FakeFileSystemSource FoldersAlone()
    {
        var source = new FakeFileSystemSource();
        source.Folders[string.Empty] = new EntryMetadata { ModifiedAt = 1_721_000_000_000, AccessedAt = FirstRead };
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = 1_721_000_000_000, AccessedAt = FirstRead };
        source.Folders["docs/inner"] = new EntryMetadata { ModifiedAt = 1_721_000_000_000, AccessedAt = FirstRead };
        return source;
    }

    // What the first backup's own walk did to every folder it read.
    private static void ReadEveryFolder(FakeFileSystemSource source)
    {
        foreach (var path in source.Folders.Keys.ToList())
        {
            source.Folders[path] = source.Folders[path] with { AccessedAt = SecondRead };
        }
    }

    private static async Task<TreeManifest> ReadTreeAsync(RepositoryKeySet keys, IObjectStore store, ObjectId treeId)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(treeId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return TreeManifestCodec.Decode(read.Plaintext!);
    }

    [TestMethod]
    public async Task OnlyTheFoldersAccessTimesMoved_TheSnapshotKeepsTheTreesItHad()
    {
        var source = Source();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, First, now: 1_722_600_000_000), CancellationToken.None);
        ReadEveryFolder(source);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source), CancellationToken.None);

        Assert.AreEqual(
            first.RootTreeObjectId, second.RootTreeObjectId,
            "a backup's own read of a folder must not make the next backup write the folder again");
        Assert.AreEqual(
            catalogue.LookupPath(First, "docs")!.ObjectId, catalogue.LookupPath(Second, "docs")!.ObjectId);
        Assert.AreEqual(FirstRead, (await ReadTreeAsync(keys, store, second.RootTreeObjectId)).Metadata!.AccessedAt);
    }

    [TestMethod]
    public async Task AFolderWhoseModeChanged_GetsANewTree_StatingTheAccessTimeItHasNow()
    {
        var source = Source();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, First, now: 1_722_600_000_000), CancellationToken.None);
        ReadEveryFolder(source);
        source.Folders["docs"] = source.Folders["docs"] with { PosixMode = 0x1C0 };
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source), CancellationToken.None);

        var docs = catalogue.LookupPath(Second, "docs")!.ObjectId;
        Assert.AreNotEqual(catalogue.LookupPath(First, "docs")!.ObjectId, docs);
        var tree = await ReadTreeAsync(keys, store, docs);
        Assert.AreEqual(0x1C0u, tree.Metadata!.PosixMode);
        Assert.AreEqual(SecondRead, tree.Metadata.AccessedAt);
    }

    [TestMethod]
    public async Task AFolderWhoseFileChanged_GetsANewTree_StatingTheAccessTimeItHasNow()
    {
        var source = Source();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, First, now: 1_722_600_000_000), CancellationToken.None);
        ReadEveryFolder(source);
        source.AddFile("docs/notes.txt", [1, 2, 3]);
        var second = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(After(source), CancellationToken.None);

        var docs = catalogue.LookupPath(Second, "docs")!.ObjectId;
        Assert.AreNotEqual(catalogue.LookupPath(First, "docs")!.ObjectId, docs);
        Assert.AreEqual(SecondRead, (await ReadTreeAsync(keys, store, docs)).Metadata!.AccessedAt);
        Assert.AreEqual(SecondRead, (await ReadTreeAsync(keys, store, second.RootTreeObjectId)).Metadata!.AccessedAt);
    }

    // A folder too large for one tree is judged by the identity of its head,
    // which names its continuations; an identity derived from the head's own
    // entries alone would never match, and such a folder would be written
    // again on every backup.
    [TestMethod]
    public async Task AFolderWrittenAsAChain_IsJudgedByTheIdentityItsChainIsWrittenUnder()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        await using var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, SmallBlobPolicy.BlobWriteProfile, FormatVersions.SealedDataPlane);
        var entries = Enumerable.Range(0, 40)
            .Select(i => new TreeEntry(
                System.Text.Encoding.UTF8.GetBytes($"file-{i:D3}.txt"), ObjectId.FromBytes(new byte[32]), EntryKind.File))
            .ToList();
        var metadata = new EntryMetadata { ModifiedAt = 1_721_000_000_000, AccessedAt = FirstRead };
        var written = new List<ObjectId>();

        var head = await TreeChainWriter.WriteAsync(
            builder, entries, "docs"u8.ToArray(), NameNormalisation.Nfc, metadata, CancellationToken.None,
            shardBudget: 256, written: written);

        Assert.IsGreaterThan(1, written.Count, "a budget this small writes forty entries as a chain");
        Assert.AreEqual(
            head,
            TreeChainWriter.HeadIdOf(builder, entries, "docs"u8.ToArray(), NameNormalisation.Nfc, metadata, shardBudget: 256));
    }

    [TestMethod]
    public async Task OnlyTheFoldersAccessTimesMoved_AfterTheCatalogueIsProjectedFromTheRepository_TheTreesAreKept()
    {
        var source = FoldersAlone();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        ObjectId firstRoot;
        using (var live = OpenCatalogue("live.db"))
        {
            firstRoot = (await CreateOrchestrator(store, keys, credential, live)
                .PublishAsync(Job(source, First, now: 1_722_600_000_000), CancellationToken.None)).RootTreeObjectId;
        }

        using var rebuilt = OpenCatalogue();
        using (var loader = new IndexLoader(store, Repo, credential))
        {
            await new CatalogueRebuilder(loader).RebuildAsync(
                rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
                CancellationToken.None);
        }

        using (var reader = new RepositoryReader(Repo, keys, store, Authority))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            await CatalogueProjector.ProjectAsync(rebuilt, reader, store, Repo, keys, credential, CancellationToken.None);
        }

        ReadEveryFolder(source);
        var second = await CreateOrchestrator(store, keys, credential, rebuilt)
            .PublishAsync(After(source), CancellationToken.None);

        Assert.AreEqual(firstRoot, second.RootTreeObjectId);
        Assert.AreEqual(
            rebuilt.LookupPath(First, "docs/inner")!.ObjectId, rebuilt.LookupPath(Second, "docs/inner")!.ObjectId);
    }

    [TestMethod]
    public async Task OnlyTheFoldersAccessTimesMoved_AfterAForensicRebuild_TheTreesAreKept()
    {
        var source = FoldersAlone();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        ObjectId firstRoot;
        using (var live = OpenCatalogue("live.db"))
        {
            firstRoot = (await CreateOrchestrator(store, keys, credential, live)
                .PublishAsync(Job(source, First, now: 1_722_600_000_000), CancellationToken.None)).RootTreeObjectId;
        }

        using var rebuilt = OpenCatalogue();
        using (var rebuilder = new ForensicRebuilder(store, Repo, credential))
        {
            var report = await rebuilder.RebuildAsync(
                rebuilt, new ForensicTarget.Everything(), CancellationToken.None);
            Assert.IsTrue(report.TargetSatisfied, string.Join("; ", report.Findings.Select(finding => finding.Detail)));
        }

        ReadEveryFolder(source);
        var second = await CreateOrchestrator(store, keys, credential, rebuilt)
            .PublishAsync(After(source), CancellationToken.None);

        Assert.AreEqual(firstRoot, second.RootTreeObjectId);
        Assert.AreEqual(
            rebuilt.LookupPath(First, "docs/inner")!.ObjectId, rebuilt.LookupPath(Second, "docs/inner")!.ObjectId);
    }
}
