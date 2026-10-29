using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Index;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// The catalogue's contract (architecture 02 §7; FR-MAN-002, FR-MAN-005;
/// NFR-PERF-004, NFR-PERF-010): a disposable cache whose SQL location
/// resolver agrees with <see cref="IndexPrecedence"/> on every input — two
/// implementations of 07 §3 that must never diverge. Also (FR-VER-001) that
/// the signed blob digests every delta publishes survive a rebuild from the
/// index plane alone, which is what the digest tier of verification reads.
/// And that each commit is atomic but not flushed, the protection
/// ADR-0010 Amendment 2 gives a cache. And that a catalogue it creates —
/// new, or in place of one another schema wrote — says it needs rebuilding
/// until a rebuild says otherwise (FR-MAN-002): a cache discarded is safe
/// only while something fills it again. A location names its blob and its
/// writer by numbers of the catalogue's own, and still ranks by the writer's
/// id (NFR-PERF-011, ADR-0144). A tree entry is keyed by its folder and its
/// name, and its path, a folder's listing and a snapshot's path order are as
/// they were (NFR-PERF-011, ADR-0148). A folder's entries are kept once,
/// under its tree, so snapshots that hold it unchanged share them, and
/// forgetting a snapshot lets go of only what none of the rest reaches
/// (NFR-PERF-011, ADR-0151). A folder's access time goes with the last
/// snapshot that reaches its tree, and stays while any does (NFR-PERF-011,
/// ADR-0153).
/// </summary>
[TestClass]
public sealed class CatalogueTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-catalogue-tests", Guid.NewGuid().ToString("n"));

    private string CataloguePath => Path.Combine(_root, "catalogue.db");

    private Catalogue Open() => Catalogue.Open(CataloguePath, Repo);

    private static ObjectId Object(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static BlobId Blob(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return BlobId.FromBytes(bytes);
    }

    private static WriterId Writer(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return WriterId.FromBytes(bytes);
    }

    private static byte[] Snapshot(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return bytes;
    }

    private static DeltaId Delta(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return DeltaId.FromBytes(bytes);
    }

    [TestMethod]
    public void ApplyDelta_TheSameDeltaTwice_IsANoOp()
    {
        using var catalogue = Open();

        var delta = new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        };

        catalogue.ApplyDelta(Delta(1), delta);
        catalogue.ApplyDelta(Delta(1), delta);

        Assert.AreEqual(1, catalogue.AppliedDeltaCount());
        Assert.IsNotNull(catalogue.ResolveLocation(Object(1)));
    }

    [TestMethod]
    public void ResolveLocation_TheLaterIndexGeneration_Wins_WhicheverWriterPublishedIt()
    {
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(9),
            Sequence = 1,
            Generation = 0,
            IndexGeneration = 1,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(2),
            Sequence = 1,
            Generation = 0,
            IndexGeneration = 2,
            Entries = [new IndexEntry(Object(1), Blob(2), 88, 100, 1, 1, IndexEntryType.Supersession)],
        });

        var winner = catalogue.ResolveLocation(Object(1))!;
        Assert.AreEqual(Blob(2), winner.BlobId);
        Assert.AreEqual(2UL, winner.Generation);
        Assert.AreEqual(Blob(2), catalogue.WinningLocation(Object(1))!.BlobId);
    }

    [TestMethod]
    public void MaxIndexGeneration_IsTheHighestAnyAppliedDeltaCarries()
    {
        // What a format-4 writer publishes one above (07 §2, key 12).
        using var catalogue = Open();
        Assert.AreEqual(0UL, catalogue.MaxIndexGeneration());

        catalogue.ApplyDelta(Delta(1), new IndexDelta { WriterId = Writer(1), Sequence = 1, Generation = 0, IndexGeneration = 5 });
        catalogue.ApplyDelta(Delta(2), new IndexDelta { WriterId = Writer(2), Sequence = 1, Generation = 0, IndexGeneration = 3 });
        catalogue.ApplyDelta(Delta(3), new IndexDelta { WriterId = Writer(3), Sequence = 1, Generation = 0 });

        Assert.AreEqual(5UL, catalogue.MaxIndexGeneration());
    }

    [TestMethod]
    public void ResolveLocation_TwoWritersAtOneGeneration_RankByTheirIds_NotByWhichTheCatalogueMetFirst()
    {
        // The catalogue names a writer by a number of its own (ADR-0144),
        // given in the order it meets them; 07 §3 ranks by the writer's id.
        using var catalogue = Open();
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(0x90),
            Sequence = 1,
            Generation = 0,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(0x10),
            Sequence = 1,
            Generation = 0,
            Entries = [new IndexEntry(Object(1), Blob(2), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        var winner = catalogue.ResolveLocation(Object(1))!;

        Assert.AreEqual(Writer(0x90), winner.WriterId, "the higher id wins a tie, though the catalogue met it first");
        Assert.AreEqual(Blob(1), winner.BlobId);
    }

    [TestMethod]
    public void FindReuseCandidate_OfTwoWritersCopies_NamesTheWriterAReadResolvesTo()
    {
        using var catalogue = Open();
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(0x90),
            Sequence = 1,
            Generation = 0,
            IndexGeneration = 1,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(0x10),
            Sequence = 1,
            Generation = 0,
            IndexGeneration = 2,
            Entries = [new IndexEntry(Object(1), Blob(2), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.RecordVerified(Object(1));

        var candidate = catalogue.FindReuseCandidate(Object(1))!;

        Assert.AreEqual(Writer(0x10), candidate.WriterId, "the later generation's writer, whose id sorts lower");
        Assert.AreEqual(catalogue.ResolveLocation(Object(1))!.WriterId, candidate.WriterId);
        Assert.IsTrue(candidate.Verified);
    }

    [TestMethod]
    public void HasLocation_AnObjectWhoseOnlyLocationIsInADeletedBlob_HasNone()
    {
        using var catalogue = Open();
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        Assert.IsTrue(catalogue.HasLocation(Object(1)));

        catalogue.SetBlobState(Blob(1), BlobState.Deleted);

        Assert.IsFalse(catalogue.HasLocation(Object(1)));
        Assert.IsFalse(catalogue.HasLocationBy(Object(1), Writer(1)));
        Assert.IsNull(catalogue.FindReuseCandidate(Object(1)));
    }

    [TestMethod]
    public void LookupPath_CaseInsensitive_FollowsEveryFolderWhoseNameFoldsAlike()
    {
        // The fold is taken as the path is asked for, a folder at a time
        // (ADR-0146): two folders may fold alike, and only one of them holds
        // what is asked.
        using var catalogue = Open();
        var snapshot = Snapshot(0x42);
        CatalogueTrees.RecordSnapshot(
            catalogue,
            snapshot,
            ("Docs", EntryKind.DirectoryPlaceholder, Object(1)),
            ("Docs/x.txt", EntryKind.File, Object(2)),
            ("DOCS", EntryKind.DirectoryPlaceholder, Object(3)),
            ("DOCS/y.txt", EntryKind.File, Object(4)));

        Assert.AreEqual("Docs/x.txt", catalogue.LookupPath(snapshot, "docs/X.TXT", caseInsensitive: true)?.Path);
        Assert.AreEqual("DOCS/y.txt", catalogue.LookupPath(snapshot, "docs/Y.txt", caseInsensitive: true)?.Path);
        Assert.AreEqual(Object(4), catalogue.LookupPath(snapshot, "docs/Y.txt", caseInsensitive: true)?.ObjectId);
        Assert.IsNull(catalogue.LookupPath(snapshot, "docs/z.txt", caseInsensitive: true));
        Assert.IsNull(catalogue.LookupPath(snapshot, "docs/X.TXT"), "a lookup that is not asked to fold does not");
    }

    [TestMethod]
    public void LookupPath_CaseInsensitive_PrefersTheExactPath_ThenTheFirstInOrdinalOrder()
    {
        using var catalogue = Open();
        var snapshot = Snapshot(0x43);
        CatalogueTrees.RecordSnapshot(
            catalogue,
            snapshot,
            ("Docs", EntryKind.DirectoryPlaceholder, Object(1)),
            ("Docs/x.txt", EntryKind.File, Object(2)),
            ("docs", EntryKind.DirectoryPlaceholder, Object(3)),
            ("docs/x.txt", EntryKind.File, Object(4)));

        Assert.AreEqual("docs/x.txt", catalogue.LookupPath(snapshot, "docs/x.txt", caseInsensitive: true)?.Path);
        Assert.AreEqual("Docs/x.txt", catalogue.LookupPath(snapshot, "Docs/x.txt", caseInsensitive: true)?.Path);
        Assert.AreEqual("Docs/x.txt", catalogue.LookupPath(snapshot, "DOCS/X.TXT", caseInsensitive: true)?.Path);
    }

    [TestMethod]
    public void LookupPath_CaseInsensitive_FoldsTheComposedAndDecomposedFormsAlike()
    {
        using var catalogue = Open();
        var snapshot = Snapshot(0x44);
        CatalogueTrees.RecordSnapshot(
            catalogue,
            snapshot,
            ("Cafe\u0301", EntryKind.DirectoryPlaceholder, Object(1)),
            ("Cafe\u0301/menu.txt", EntryKind.File, Object(2)));

        Assert.AreEqual(Object(2), catalogue.LookupPath(snapshot, "CAF\u00c9/MENU.TXT", caseInsensitive: true)?.ObjectId);
    }

    [TestMethod]
    public void RecordFolder_ANameThatHoldsASeparator_IsListed_ButIsNoPathsComponent()
    {
        // A folder's entries are kept by name under its tree (ADR-0151), and a
        // path is followed a name at a time. A name that holds a separator,
        // which no codec refuses and no capture makes, is listed as it is
        // and is never what a path reaches.
        using var catalogue = Open();
        var snapshot = Snapshot(0x45);
        var root = Object(0x30);
        catalogue.RecordFolder(
            root,
            [new CatalogueFolderEntry("/x", EntryKind.File, Object(2)), new CatalogueFolderEntry("x", EntryKind.File, Object(1))]);
        catalogue.RecordSnapshot(
            snapshot, new byte[16], new byte[16], Object(0x31), root, publicationGeneration: 0,
            captureStatus: 1, signatureState: 1);

        Assert.AreEqual(Object(1), catalogue.LookupPath(snapshot, "x")?.ObjectId);
        Assert.IsNull(catalogue.LookupPath(snapshot, "/x"));
        CollectionAssert.AreEqual(
            new[] { "/x", "x" }, catalogue.ListDirectory(snapshot, string.Empty).Select(entry => entry.Path).ToArray());
    }

    [TestMethod]
    public void EnumerateLeaves_IsInPathOrder_AcrossFolders()
    {
        // A folder's files sort after a sibling whose name has a lower byte
        // than the separator, and before one whose name has a higher: path
        // order is not the order of folders and then names.
        using var catalogue = Open();
        var snapshot = Snapshot(0x46);
        CatalogueTrees.RecordSnapshot(
            catalogue,
            snapshot,
            ("a0", EntryKind.File, Object(1)),
            ("a", EntryKind.DirectoryPlaceholder, Object(2)),
            ("a/z", EntryKind.File, Object(3)),
            ("a-b", EntryKind.File, Object(4)));

        CollectionAssert.AreEqual(
            new[] { "a-b", "a/z", "a0" }, catalogue.EnumerateLeaves(snapshot).Select(entry => entry.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { "a", "a-b", "a0" }, catalogue.ListDirectory(snapshot, string.Empty).Select(entry => entry.Path).ToArray());
        Assert.AreEqual(Object(2), catalogue.LookupDirectory(snapshot, "a")?.TreeId);
        Assert.IsNull(catalogue.LookupDirectory(snapshot, "a0"), "a file is no folder, whatever it names");
        Assert.AreEqual("a/z", Assert.ContainsSingle(catalogue.ListDirectory(snapshot, "a")).Path);
    }

    [TestMethod]
    public void LookupDirectory_TwoFoldersOfOneName_AreEachFoundByTheirOwnPath()
    {
        using var catalogue = Open();
        var snapshot = Snapshot(0x47);
        CatalogueTrees.RecordSnapshot(
            catalogue,
            snapshot,
            ("a", EntryKind.DirectoryPlaceholder, Object(1)),
            ("a/x", EntryKind.DirectoryPlaceholder, Object(2)),
            ("b", EntryKind.DirectoryPlaceholder, Object(3)),
            ("b/x", EntryKind.DirectoryPlaceholder, Object(4)));

        Assert.AreEqual(Object(2), catalogue.LookupDirectory(snapshot, "a/x")?.TreeId);
        Assert.AreEqual(Object(4), catalogue.LookupDirectory(snapshot, "b/x")?.TreeId);
    }

    [TestMethod]
    public void TwoSnapshotsHoldingOneFolder_KeepItsEntriesOnce_AndEachFindsThemAtItsPath()
    {
        // A folder's entries are kept under its tree (ADR-0151), so a
        // snapshot that left a folder as it was reaches the rows an earlier
        // one recorded, and pays nothing for them.
        using var catalogue = Open();
        var (first, second) = SnapshotsSharingDocs(catalogue);

        // Each root holds docs and b.txt, and docs holds a.txt, once.
        Assert.AreEqual(5L, FolderEntries());
        foreach (var snapshot in new[] { first, second })
        {
            Assert.AreEqual(Object(0x11), catalogue.LookupPath(snapshot, "docs/a.txt")?.ObjectId);
            Assert.AreEqual("docs/a.txt", Assert.ContainsSingle(catalogue.ListDirectory(snapshot, "docs")).Path);
            Assert.AreEqual(Object(0x10), catalogue.LookupDirectory(snapshot, "docs")?.TreeId);
            Assert.AreEqual(2L, catalogue.CountFiles(snapshot));
        }

        Assert.AreEqual(Object(0x12), catalogue.LookupPath(first, "b.txt")?.ObjectId);
        Assert.AreEqual(Object(0x13), catalogue.LookupPath(second, "b.txt")?.ObjectId);
        CollectionAssert.AreEqual(
            new[] { "b.txt", "docs/a.txt" }, catalogue.EnumerateLeaves(second).Select(entry => entry.Path).ToArray());
    }

    [TestMethod]
    public void ForgettingASnapshot_KeepsTheFoldersAnotherStillReaches_AndLetsTheRestGo()
    {
        using var catalogue = Open();
        var (first, second) = SnapshotsSharingDocs(catalogue);
        Assert.IsNotNull(catalogue.LookupPath(first, "docs/a.txt"));

        catalogue.ForgetSnapshot(first);

        // docs is still held, by the second, so only the first's row going
        // keeps the first from reaching it.
        Assert.IsNull(catalogue.LookupPath(first, "docs/a.txt"));
        Assert.AreEqual(Object(0x11), catalogue.LookupPath(second, "docs/a.txt")?.ObjectId);
        Assert.AreEqual(3L, FolderEntries(), "the first root's entries go, and docs stays with the second");

        catalogue.ForgetSnapshot(second);

        Assert.AreEqual(0L, FolderEntries(), "nothing reaches docs any more");
    }

    [TestMethod]
    public void ForgettingASnapshot_LetsGoOfTheAccessTimeOfEachFolderNoneOfTheRestReaches()
    {
        // A folder's access time is kept under its tree, as its entries are,
        // so it goes when the last snapshot reaching that tree does
        // (ADR-0153).
        using var catalogue = Open();
        var (first, second) = SnapshotsSharingDocs(catalogue);
        var (firstRoot, secondRoot) = (catalogue.LookupDirectory(first, "")!.TreeId, catalogue.LookupDirectory(second, "")!.TreeId);
        catalogue.RecordDirectoryAccessTimes([(firstRoot, 100UL), (secondRoot, 200UL), (Object(0x10), 300UL)]);

        catalogue.ForgetSnapshot(first);

        Assert.AreEqual(2L, AccessTimes(), "the first root's time goes, and docs's stays with the second");
        Assert.AreEqual(200UL, catalogue.LookupDirectory(second, "")?.AccessedAt);
        Assert.AreEqual(300UL, catalogue.LookupDirectory(second, "docs")?.AccessedAt);

        catalogue.ForgetSnapshot(second);

        Assert.AreEqual(0L, AccessTimes(), "nothing reaches either folder any more");
    }

    [TestMethod]
    public void ForgettingASnapshot_KeepsTheAccessTimeOfAnEmptyFolderAnotherStillReaches()
    {
        // An empty folder holds no entries of its own. It is reached through
        // its parent's, and so is its access time.
        using var catalogue = Open();
        var (kept, forgotten) = (Snapshot(0x4D), Snapshot(0x4E));
        CatalogueTrees.RecordSnapshot(catalogue, kept, ("empty", EntryKind.DirectoryPlaceholder, Object(0x20)));
        CatalogueTrees.RecordSnapshot(catalogue, forgotten, ("other.txt", EntryKind.File, Object(0x21)));
        catalogue.RecordDirectoryAccessTimes([(Object(0x20), 400UL)]);

        catalogue.ForgetSnapshot(forgotten);

        Assert.AreEqual(400UL, catalogue.LookupDirectory(kept, "empty")?.AccessedAt);
    }

    [TestMethod]
    public void RecordDirectoryAccessTimes_ATreeAlreadyTimed_KeepsTheTimeItStates()
    {
        // A tree's identity derives from its bytes, which state its access
        // time, so one tree states one time wherever it is met.
        using var catalogue = Open();
        var snapshot = Snapshot(0x4F);
        CatalogueTrees.RecordSnapshot(catalogue, snapshot, ("docs", EntryKind.DirectoryPlaceholder, Object(0x22)));

        catalogue.RecordDirectoryAccessTimes([(Object(0x22), 500UL)]);
        catalogue.RecordDirectoryAccessTimes([(Object(0x22), 600UL)]);

        Assert.AreEqual(500UL, catalogue.LookupDirectory(snapshot, "docs")?.AccessedAt);
    }

    [TestMethod]
    public void LookupPath_ASnapshotRecordedAgain_IsReadFromItsNewTree()
    {
        // A lookup resolves a folder once for the files asked in it in turn,
        // and that must not outlive the snapshot's row.
        using var catalogue = Open();
        var snapshot = Snapshot(0x4B);
        var (before, after) = (Object(0x30), Object(0x32));
        CatalogueTrees.Record(
            catalogue, [("docs", EntryKind.DirectoryPlaceholder, Object(0x10)), ("docs/a.txt", EntryKind.File, Object(0x11))], before);
        CatalogueTrees.Record(
            catalogue, [("docs", EntryKind.DirectoryPlaceholder, Object(0x14)), ("docs/b.txt", EntryKind.File, Object(0x15))], after);
        Record(before);
        Assert.IsNotNull(catalogue.LookupPath(snapshot, "docs/a.txt"));

        // Only the row changes: both trees are already held.
        Record(after);

        Assert.IsNull(catalogue.LookupPath(snapshot, "docs/a.txt"));
        Assert.AreEqual(Object(0x15), catalogue.LookupPath(snapshot, "docs/b.txt")?.ObjectId);

        void Record(ObjectId root) =>
            catalogue.RecordSnapshot(
                snapshot, new byte[16], new byte[16], Object(0x31), root, publicationGeneration: 0,
                captureStatus: 1, signatureState: 1);
    }

    [TestMethod]
    public void LookupPath_AFolderRecordedAfterALookupMissedIt_IsFound()
    {
        using var catalogue = Open();
        var snapshot = Snapshot(0x4C);
        var root = Object(0x30);
        catalogue.RecordSnapshot(
            snapshot, new byte[16], new byte[16], Object(0x31), root, publicationGeneration: 0,
            captureStatus: 1, signatureState: 1);
        Assert.IsNull(catalogue.LookupPath(snapshot, "docs/a.txt"));

        CatalogueTrees.Record(
            catalogue, [("docs", EntryKind.DirectoryPlaceholder, Object(0x10)), ("docs/a.txt", EntryKind.File, Object(0x11))], root);

        Assert.AreEqual(Object(0x11), catalogue.LookupPath(snapshot, "docs/a.txt")?.ObjectId);
    }

    /// <summary>
    /// Two snapshots whose roots differ by <c>b.txt</c> and which hold one
    /// <c>docs</c> folder, tree <c>0x10</c>, holding <c>a.txt</c>.
    /// </summary>
    private static (byte[] First, byte[] Second) SnapshotsSharingDocs(Catalogue catalogue)
    {
        var (first, second) = (Snapshot(0x49), Snapshot(0x4A));
        CatalogueTrees.RecordSnapshot(
            catalogue,
            first,
            ("docs", EntryKind.DirectoryPlaceholder, Object(0x10)),
            ("docs/a.txt", EntryKind.File, Object(0x11)),
            ("b.txt", EntryKind.File, Object(0x12)));
        CatalogueTrees.RecordSnapshot(
            catalogue,
            second,
            ("docs", EntryKind.DirectoryPlaceholder, Object(0x10)),
            ("docs/a.txt", EntryKind.File, Object(0x11)),
            ("b.txt", EntryKind.File, Object(0x13)));
        return (first, second);
    }

    /// <summary>How many folder entries the catalogue keeps, read beside the connection under test.</summary>
    private long FolderEntries() => Rows("folder_entries");

    /// <summary>How many folder access times the catalogue keeps, read beside the connection under test.</summary>
    private long AccessTimes() => Rows("directory_access_times");

    private long Rows(string table)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = CataloguePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [TestMethod]
    public void ContentOf_TwoFilesOfOneName_InTwoFolders_AreTwoFiles()
    {
        using var catalogue = Open();
        var snapshot = TwoFilesOfOneName(catalogue);

        var content = catalogue.ContentOf(snapshot);

        Assert.AreEqual(2L, content.Files);
        Assert.HasCount(2, content.FileBlobs);
        Assert.HasCount(2, content.Blobs);
    }

    [TestMethod]
    public void FilesDeliveredThrough_TwoFilesOfOneName_InTwoFolders_AreCountedApart()
    {
        // The snapshot's record is published at sequence 2 and both files'
        // content later, at 3, so a destination synced through 1 holds the
        // record's run and neither file.
        using var catalogue = Open();
        var snapshot = TwoFilesOfOneName(catalogue);

        var delivered = catalogue.FilesDeliveredThrough(snapshot, Writer(0x77), syncedSequence: 1);

        Assert.AreEqual(2L, delivered.Total);
        Assert.AreEqual(0L, delivered.Held);
    }

    /// <summary>
    /// A snapshot whose record one writer publishes at sequence 2, holding
    /// <c>a/report.txt</c> and <c>b/report.txt</c>, each needing content of
    /// its own in a blob of its own, which the writer publishes at 3.
    /// </summary>
    private static byte[] TwoFilesOfOneName(Catalogue catalogue)
    {
        var snapshot = Snapshot(0x48);
        var (manifest, root) = (Object(0x40), Object(0x41));
        catalogue.RecordSnapshot(
            snapshot, new byte[16], new byte[16], manifest, root, publicationGeneration: 0,
            captureStatus: 1, signatureState: 1);

        var entries = new List<(string, EntryKind, ObjectId)>();
        foreach (var (folder, version, content) in (ReadOnlySpan<(string, byte, byte)>)[("a", 0x51, 0x61), ("b", 0x52, 0x62)])
        {
            entries.Add((folder, EntryKind.DirectoryPlaceholder, Object((byte)(version + 0x10))));
            entries.Add((folder + "/report.txt", EntryKind.File, Object(version)));
            catalogue.RecordVersionContents(Object(version), [Object(content)]);
        }

        CatalogueTrees.Record(catalogue, entries, root);

        void Publish(ulong sequence, params (ObjectId Object, BlobId Blob)[] placed) =>
            catalogue.ApplyDelta(
                Delta((byte)sequence),
                new IndexDelta
                {
                    WriterId = Writer(0x77),
                    Sequence = sequence,
                    Generation = 0,
                    Entries = [.. placed.Select(item => new IndexEntry(item.Object, item.Blob, 88, 100, 1, 1, IndexEntryType.Insertion))],
                });

        Publish(2, (manifest, Blob(0xA0)));
        Publish(3, (Object(0x61), Blob(0xA1)), (Object(0x62), Blob(0xA2)));

        return snapshot;
    }

    [TestMethod]
    public void ResolveLocation_AnyRandomisedEntrySet_AgreesWithIndexPrecedence()
    {
        // Two implementations of 07 §3 — the in-memory resolver and the SQL
        // ORDER BY — must never diverge. Randomized parity over many objects
        // is the strongest cheap check.
        using var catalogue = Open();
        var random = new Random(20260803);
        var byObject = new Dictionary<ObjectId, List<ProvenancedEntry>>();
        var sequence = 0UL;

        for (var i = 0; i < 200; i++)
        {
            var objectId = Object((byte)random.Next(1, 20));
            var entry = new IndexEntry(
                objectId,
                Blob((byte)random.Next(1, 10)),
                (ulong)random.Next(0, 100_000),
                (uint)random.Next(1, 10_000),
                0x0001,
                0x0001,
                random.Next(2) == 0 ? IndexEntryType.Insertion : IndexEntryType.Supersession);

            // The rank generation is the delta's index generation (07 §3,
            // format 4); a delta without one, as every format-3 delta is,
            // ranks at zero. The key generation names a signing key and no
            // longer ranks anything.
            var generation = (ulong)random.Next(0, 4);
            var writer = Writer((byte)random.Next(1, 5));
            sequence++;

            catalogue.ApplyDelta(
                DeltaId.FromBytes(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(sequence)).AsSpan(0, 16)),
                new IndexDelta
                {
                    WriterId = writer,
                    Sequence = sequence,
                    Generation = 0,
                    IndexGeneration = generation == 0 ? null : generation,
                    Entries = [entry],
                });

            byObject.TryAdd(objectId, []);
            byObject[objectId].Add(new ProvenancedEntry(entry, generation, writer, sequence));
        }

        foreach (var (objectId, candidates) in byObject)
        {
            var expected = IndexPrecedence.Resolve(candidates, _ => BlobState.Live, [])!;
            var actual = catalogue.ResolveLocation(objectId)!;

            Assert.AreEqual(expected.Entry.BlobId, actual.BlobId);
            Assert.AreEqual(expected.Entry.PhysicalOffset, actual.PhysicalOffset);
            Assert.AreEqual(expected.Generation, actual.Generation);
            Assert.AreEqual(expected.WriterId, actual.WriterId);
            Assert.AreEqual(expected.Sequence, actual.Sequence);
        }
    }

    [TestMethod]
    public void ResolveLocation_TheWinningEntryNamesADeletedBlob_ExcludesItAndReportsAFinding()
    {
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            IndexGeneration = 1,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 2,
            Generation = 0,
            IndexGeneration = 2,
            Entries = [new IndexEntry(Object(1), Blob(2), 999, 100, 1, 1, IndexEntryType.Supersession)],
        });

        catalogue.SetBlobState(Blob(2), BlobState.Deleted);

        var resolved = catalogue.ResolveLocation(Object(1));

        // The generation-2 winner names a deleted blob: superseded (rule 3),
        // the generation-1 location serves, and the anomaly is recorded.
        Assert.AreEqual(Blob(1), resolved!.BlobId);
        Assert.Contains(finding => finding.Kind == DamageKind.MissingBlob, catalogue.Findings());
    }

    [TestMethod]
    public void ApplyDelta_TheCoveredBlobDigests_SurviveARebuildFromTheDeltaAlone()
    {
        // A rebuilt catalogue has only the index plane to go on: no
        // RecordBlob ever ran, so the physical row is a placeholder. The
        // digest the writer signed into the delta is still there — and the
        // placeholder's store key is NOT, since it would be the blob id
        // dressed as a key, and a restore from a rebuilt catalogue derives
        // the real one.
        using var catalogue = Open();
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(digest.AsSpan().SequenceEqual(catalogue.SignedDigestOf(Blob(1))!.Value.Span));
        Assert.IsFalse(catalogue.SignedDigestOf(Blob(2)).HasValue, "no delta named blob 2");

        var resolved = catalogue.ResolveLocation(Object(1))!;
        Assert.AreEqual(Blob(1), resolved.BlobId);
        Assert.IsFalse(resolved.StoreBlobKey.HasValue, "a placeholder row must not answer with the blob id as a store key");
    }

    [TestMethod]
    public void ApplyDelta_ARowTheWriterRecorded_KeepsItsStoreKeyAndGainsTheDigest()
    {
        // The live path: RecordBlob first with the real store key, the delta
        // after it. The delta's digest lands and nothing physical moves.
        using var catalogue = Open();
        var storeKey = StoreBlobKey.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);

        catalogue.RecordBlob(Blob(1), storeKey, BlobClass.Data, KeyGeneration.Zero, 1, 4096, digest: default);
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.AreEqual(storeKey, catalogue.ResolveLocation(Object(1))!.StoreBlobKey);
        Assert.IsTrue(digest.AsSpan().SequenceEqual(catalogue.SignedDigestOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ADeltaWithoutDigests_RecordsNothingAboutItsBlobs()
    {
        // The digests are optional in the delta (07 §2.2); a writer that
        // published none leaves the tier with nothing to read, and the
        // catalogue must not invent a row that says otherwise.
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsFalse(catalogue.SignedDigestOf(Blob(1)).HasValue, "the delta carried no digest");
        Assert.IsFalse(catalogue.ResolveLocation(Object(1))!.StoreBlobKey.HasValue);
    }

    [TestMethod]
    public void ApplyDelta_TheCoveredBlobMerkleRoots_SurviveARebuildAndNeverErasedByALaterDelta()
    {
        // The root rides the same placeholder-aware path as the digest
        // (07 §2.3), and its own statement rather than a second column on
        // the digest's: a later delta that carries digests and no roots —
        // which is every delta a format-2 writer publishes — must leave a
        // root already on record alone rather than clearing it.
        using var catalogue = Open();
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);
        var root = System.Security.Cryptography.SHA256.HashData("the tree over the sealed bytes"u8);

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            CoveredBlobMerkleRoots = [root],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
        Assert.IsFalse(catalogue.SignedMerkleRootOf(Blob(2)).HasValue, "no delta named blob 2");
        Assert.IsFalse(
            catalogue.ResolveLocation(Object(1))!.StoreBlobKey.HasValue,
            "a placeholder row must not answer with the blob id as a store key");

        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 2,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(2), Blob(1), 188, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ARowTheWriterRecorded_KeepsItsStoreKeyAndGainsTheMerkleRoot()
    {
        using var catalogue = Open();
        var storeKey = StoreBlobKey.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);
        var root = System.Security.Cryptography.SHA256.HashData("the tree over the sealed bytes"u8);

        catalogue.RecordBlob(Blob(1), storeKey, BlobClass.Data, KeyGeneration.Zero, 1, 4096, digest: default);
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            CoveredBlobMerkleRoots = [root],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.AreEqual(storeKey, catalogue.ResolveLocation(Object(1))!.StoreBlobKey);
        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ADeltaWithoutMerkleRoots_RecordsNoRoot()
    {
        // Every format-2 delta, and the case the tier reads as "this blob
        // cannot be challenged by chunk" rather than as damage.
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8)],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(catalogue.SignedDigestOf(Blob(1)).HasValue);
        Assert.IsFalse(catalogue.SignedMerkleRootOf(Blob(1)).HasValue, "the delta carried no Merkle root");
    }

    [TestMethod]
    public void Open_RepositoryIdentityDiffers_DropsAndRebuildsTheCache()
    {
        using (var catalogue = Open())
        {
            catalogue.ApplyDelta(Delta(1), new IndexDelta
            {
                WriterId = Writer(1),
                Sequence = 1,
                Generation = 0,
                Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
            });
        }

        var otherRepository = RepositoryId.FromBytes(Convert.FromHexString("ffffffffffffffffffffffffffffffff"));

        using var reopened = Catalogue.Open(CataloguePath, otherRepository);

        // The cache belonged to a different repository: dropped, not merged
        // (FR-MAN-002 — the catalogue is disposable, never authoritative).
        Assert.AreEqual(0, reopened.AppliedDeltaCount());
        Assert.IsNull(reopened.ResolveLocation(Object(1)));
    }

    [TestMethod]
    public void Open_ACatalogueItCreates_NeedsRebuilding()
    {
        // A new file holds nothing, whatever the repository holds.
        using var catalogue = Open();

        Assert.IsTrue(catalogue.NeedsRebuild);
    }

    [TestMethod]
    public void MarkRebuilt_IsDurable_AndACatalogueOpenedAgainStaysRebuilt()
    {
        using (var catalogue = Open())
        {
            catalogue.MarkRebuilt();
            Assert.IsFalse(catalogue.NeedsRebuild);
        }

        using var reopened = Open();
        Assert.IsFalse(reopened.NeedsRebuild);
    }

    [TestMethod]
    public void Open_ACatalogueAnotherSchemaWrote_IsRecreated_AndNeedsRebuilding()
    {
        using (var catalogue = Open())
        {
            catalogue.ApplyDelta(Delta(1), new IndexDelta
            {
                WriterId = Writer(1),
                Sequence = 1,
                Generation = 0,
                Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
            });
            catalogue.MarkRebuilt();
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = CataloguePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE catalogue_info SET value = '6' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        using var reopened = Open();

        // Discarded, not migrated — and so empty, which the next reader must
        // be told rather than left to take for a repository with no history.
        Assert.AreEqual(0, reopened.AppliedDeltaCount());
        Assert.IsTrue(reopened.NeedsRebuild);
    }

    [TestMethod]
    public void Open_EachCommit_IsAtomicButNotFlushed_BecauseTheCatalogueIsACache()
    {
        using var catalogue = Open();

        var (journalMode, synchronous) = catalogue.Durability();

        // WAL keeps every commit atomic and the file consistent through a
        // crash or a power loss. NORMAL (1) is what spares each commit a disk
        // flush: a power loss can then take the newest commits, leaving the
        // catalogue behind the store, the direction StaleCatalogueTests shows
        // costs only a rewrite. FULL (2) is a flush for every row a
        // publication records, and on Windows each flush costs milliseconds.
        Assert.AreEqual("wal", journalMode);
        Assert.AreEqual(1L, synchronous);
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
