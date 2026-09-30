using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// What damaged blobs reach, as the catalogue traces it (FR-VER-005,
/// specification 04 §7): the objects a read would take from them, the file
/// versions that need those objects, and so the snapshots and paths a restore
/// of which would meet the damage. The reverse of what the manifests say,
/// kept beside them because the manifests are sealed.
/// </summary>
/// <remarks>
/// Rows written by hand, so each rule is seen alone: a version is reached
/// through any content object it needs, or through its own manifest; a
/// snapshot's own records reach the snapshot; an object whose read resolves
/// elsewhere is not reached from here at all; and an object nothing traces
/// is counted, never dropped — because a caller that cannot place damage
/// must not report the snapshots it could not rule out as untouched.
/// </remarks>
[TestClass]
public sealed class CatalogueReachTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly byte[] SnapshotA = [.. Enumerable.Repeat((byte)0xA1, 16)];
    private static readonly byte[] SnapshotB = [.. Enumerable.Repeat((byte)0xB2, 16)];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-catalogue-reach", Guid.NewGuid().ToString("n"));

    private string CataloguePath => Path.Combine(_root, "catalogue.db");

    private Catalogue Open() => Catalogue.Open(CataloguePath, Repo);

    private static ObjectId Object(byte seed) => ObjectId.FromBytes([.. Enumerable.Repeat(seed, 32)]);

    private static BlobId Blob(byte seed) => BlobId.FromBytes([.. Enumerable.Repeat(seed, 16)]);

    private static string Hex(byte[] snapshotId) => Convert.ToHexStringLower(snapshotId);

    /// <summary>Places each object in a blob, as one writer's delta at <paramref name="generation"/>.</summary>
    private static void Locate(Catalogue catalogue, byte delta, ulong generation, params (ObjectId Object, BlobId Blob)[] placed) =>
        catalogue.ApplyDelta(
            DeltaId.FromBytes([.. Enumerable.Repeat(delta, 16)]),
            new IndexDelta
            {
                WriterId = WriterId.FromBytes([.. Enumerable.Repeat((byte)0x77, 16)]),
                Sequence = delta,
                Generation = generation,
                Entries = [.. placed.Select(item => new IndexEntry(item.Object, item.Blob, 88, 100, 1, 1, IndexEntryType.Insertion))],
            });

    private static void Version(Catalogue catalogue, ObjectId version, string name, params ObjectId[] contents)
    {
        catalogue.RecordFileVersion(
            version, System.Text.Encoding.UTF8.GetBytes(name), EntryKind.File, 10, new byte[32], parentVersion: null,
            contents.Length);
        catalogue.RecordVersionContents(version, contents);
    }

    private static void Snapshot(Catalogue catalogue, byte[] snapshotId, ObjectId manifest, ObjectId rootTree)
    {
        catalogue.RecordSnapshot(
            snapshotId, new byte[16], new byte[16], manifest, rootTree, publicationGeneration: 0,
            captureStatus: 1, signatureState: 1);
        catalogue.RecordSnapshotStructure(snapshotId, manifest);
        catalogue.RecordSnapshotStructure(snapshotId, rootTree);
    }

    [TestMethod]
    public void ReachOf_ADamagedSegment_ReachesEveryVersionThatNeedsIt_AndEverySnapshotAndPathHoldingThem()
    {
        using var catalogue = Open();
        var (s1, s2, s3) = (Object(0x01), Object(0x02), Object(0x03));
        var (v1, v2) = (Object(0x11), Object(0x12));
        Locate(catalogue, 1, 0, (s1, Blob(0xD1)), (s2, Blob(0xD2)), (s3, Blob(0xD1)));
        Version(catalogue, v1, "report.txt", s1, s2);
        Version(catalogue, v2, "notes.txt", s2, s3);
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        Snapshot(catalogue, SnapshotB, Object(0x22), Object(0x32));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);
        catalogue.RecordTreeEntry(SnapshotA, "docs/notes.txt", EntryKind.File, v2);
        catalogue.RecordTreeEntry(SnapshotB, "docs/notes.txt", EntryKind.File, v2);

        var reach = catalogue.ReachOf([Blob(0xD2)], sampleLimit: 5);

        // s2 is needed by both versions; notes.txt is held by both snapshots.
        CollectionAssert.AreEquivalent(new[] { Hex(SnapshotA), Hex(SnapshotB) }, reach.Snapshots.ToList());
        Assert.AreEqual(2, reach.Files, "two distinct paths, however many snapshots hold them");
        CollectionAssert.AreEqual(new[] { "docs/notes.txt", "docs/report.txt" }, reach.FileSample.ToList());
        Assert.AreEqual(0, reach.Structures);
        Assert.AreEqual(0, reach.Untraced);
        Assert.IsTrue(reach.Complete);
    }

    [TestMethod]
    public void ReachOf_ABlobOnlyOneSnapshotNeeds_LeavesTheOtherOut()
    {
        using var catalogue = Open();
        var (s1, s3) = (Object(0x01), Object(0x03));
        var (v1, v2) = (Object(0x11), Object(0x12));
        Locate(catalogue, 1, 0, (s1, Blob(0xD1)), (s3, Blob(0xD3)));
        Version(catalogue, v1, "report.txt", s1);
        Version(catalogue, v2, "report.txt", s3);
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        Snapshot(catalogue, SnapshotB, Object(0x22), Object(0x32));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);
        catalogue.RecordTreeEntry(SnapshotB, "docs/report.txt", EntryKind.File, v2);

        var reach = catalogue.ReachOf([Blob(0xD1)], sampleLimit: 5);

        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(reach.Snapshots));
        Assert.AreEqual(1, reach.Files);
    }

    [TestMethod]
    public void ReachOf_AVersionsOwnManifest_ReachesItsPaths()
    {
        using var catalogue = Open();
        var v1 = Object(0x11);
        Locate(catalogue, 1, 0, (v1, Blob(0xE1)));
        Version(catalogue, v1, "report.txt");
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);

        var reach = catalogue.ReachOf([Blob(0xE1)], sampleLimit: 5);

        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(reach.Snapshots));
        Assert.AreEqual("docs/report.txt", Assert.ContainsSingle(reach.FileSample));
    }

    [TestMethod]
    public void ReachOf_ASnapshotsOwnRecords_ReachTheSnapshot_NotItsFiles()
    {
        using var catalogue = Open();
        var (manifest, root, continuation) = (Object(0x21), Object(0x31), Object(0x41));
        Locate(catalogue, 1, 0, (root, Blob(0xE1)), (continuation, Blob(0xE2)));
        Snapshot(catalogue, SnapshotA, manifest, root);
        catalogue.RecordSnapshotStructure(SnapshotA, continuation);
        Snapshot(catalogue, SnapshotB, Object(0x22), Object(0x32));

        var reach = catalogue.ReachOf([Blob(0xE2)], sampleLimit: 5);

        // A tree that will not read is a listing lost for a restore that
        // rebuilds from that copy, whatever its files' own bytes are.
        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(reach.Snapshots));
        Assert.AreEqual(1, reach.Structures);
        Assert.AreEqual(0, reach.Files);
    }

    [TestMethod]
    public void ReachOf_AnObjectWhoseReadResolvesToAnotherBlob_IsNotReachedFromThisOne()
    {
        // Compaction's shape: the object was copied into a newer blob at a
        // later generation, and every read now resolves there. What is left
        // in the old blob is nothing a restore would read.
        using var catalogue = Open();
        var s1 = Object(0x01);
        var v1 = Object(0x11);
        Locate(catalogue, 1, 0, (s1, Blob(0xD1)));
        Locate(catalogue, 2, 1, (s1, Blob(0xD9)));
        Version(catalogue, v1, "report.txt", s1);
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);

        var old = catalogue.ReachOf([Blob(0xD1)], sampleLimit: 5);
        var current = catalogue.ReachOf([Blob(0xD9)], sampleLimit: 5);

        Assert.IsEmpty(old.Snapshots);
        Assert.IsTrue(old.Complete, "a superseded copy is placed, and placed nowhere that matters");
        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(current.Snapshots));
    }

    [TestMethod]
    public void ReachOf_AnObjectNothingTraces_IsCounted_SoTheReachIsNotComplete()
    {
        using var catalogue = Open();
        var (traced, stray) = (Object(0x01), Object(0x0F));
        var v1 = Object(0x11);
        Locate(catalogue, 1, 0, (traced, Blob(0xD1)), (stray, Blob(0xD1)));
        Version(catalogue, v1, "report.txt", traced);
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);

        var reach = catalogue.ReachOf([Blob(0xD1)], sampleLimit: 5);

        Assert.AreEqual(1, reach.Untraced);
        Assert.IsFalse(reach.Complete, "what cannot be placed rules out nothing");
        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(reach.Snapshots), "what can be placed still is");
    }

    [TestMethod]
    public void ReachOf_ManyPaths_SamplesTheFirstInOrder_AndCountsThemAll()
    {
        using var catalogue = Open();
        var segment = Object(0x01);
        Locate(catalogue, 1, 0, (segment, Blob(0xD1)));
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        for (byte i = 0; i < 8; i++)
        {
            var version = Object((byte)(0x50 + i));
            Version(catalogue, version, $"file-{i}.txt", segment);
            catalogue.RecordTreeEntry(SnapshotA, $"docs/file-{7 - i}.txt", EntryKind.File, version);
        }

        var reach = catalogue.ReachOf([Blob(0xD1)], sampleLimit: 3);

        Assert.AreEqual(8, reach.Files);
        CollectionAssert.AreEqual(new[] { "docs/file-0.txt", "docs/file-1.txt", "docs/file-2.txt" }, reach.FileSample.ToList());
    }

    [TestMethod]
    public void ReachOf_WhileAnotherConnectionHoldsTheWriteLock_StillAnswers()
    {
        // The status read traces damage while a capture may be writing the
        // same catalogue. A trace is a read, and waiting on the write lock
        // would stall the status for as long as the capture holds it.
        using var catalogue = Open();
        var segment = Object(0x01);
        var v1 = Object(0x11);
        Locate(catalogue, 1, 0, (segment, Blob(0xD1)));
        Version(catalogue, v1, "report.txt", segment);
        Snapshot(catalogue, SnapshotA, Object(0x21), Object(0x31));
        catalogue.RecordTreeEntry(SnapshotA, "docs/report.txt", EntryKind.File, v1);

        using var writer = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = CataloguePath, Pooling = false }.ToString());
        writer.Open();
        using var held = writer.BeginTransaction(deferred: false);

        var reach = catalogue.ReachOf([Blob(0xD1)], sampleLimit: 5);

        Assert.AreEqual(Hex(SnapshotA), Assert.ContainsSingle(reach.Snapshots));
        Assert.AreEqual("docs/report.txt", Assert.ContainsSingle(reach.FileSample));
    }

    [TestMethod]
    public void LocatedBlobs_NamesEveryBlobAnyObjectIsPlacedIn_Once()
    {
        using var catalogue = Open();
        Locate(catalogue, 1, 0, (Object(0x01), Blob(0xD1)), (Object(0x02), Blob(0xD1)), (Object(0x03), Blob(0xD2)));

        CollectionAssert.AreEquivalent(new[] { Blob(0xD1), Blob(0xD2) }, catalogue.LocatedBlobs().ToList());
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
