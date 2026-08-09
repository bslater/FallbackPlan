using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Incremental backup over catalogue v2 (phase-1 wave T2): the live
/// catalogue learns each publication, an unchanged file short-circuits on
/// identity + size + mtime without its content being read (NFR-PERF-003),
/// a changed file re-archives but stores only the segments the index does
/// not already locate (09 §6; FR-MAN-006), and a rebuilt catalogue answers the same
/// queries — with the short-circuit conservatively disabled because
/// identities are not durable (02 §2) — and a deleted file leaves the earlier
/// snapshot whole, because a deletion is new snapshot state rather than a
/// removal (FR-SNP-002).
/// </summary>
[TestClass]
public sealed class IncrementalBackupTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb? catalogue) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            observer: null,
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
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

    private static FakeFileSystemSource BuildSource()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/big.bin", Deterministic(300_000, 3));
        source.AddFile("docs/small.txt", Deterministic(500, 5));
        source.AddFile("readme.md", Deterministic(2_000, 7));
        return source;
    }

    [TestMethod]
    public async Task Catalogue_AfterPublication_AnswersSnapshotsPathsAndListings()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var snapshot = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        SequenceAssert.AreEqual(Enumerable.Repeat((byte)0xA1, 16).ToArray(), snapshot.SnapshotId.ToArray());
        Assert.AreEqual(1_722_600_000_000ul, snapshot.CapturedAt);
        Assert.AreEqual(published.RootTreeObjectId, snapshot.RootTree);
        Assert.AreEqual(1, snapshot.CaptureStatus);
        Assert.AreEqual(1, snapshot.SignatureState);

        // ls / — byte order: docs, readme.md.
        var root = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);
        SequenceAssert.AreEqual(["docs", "readme.md"], root.Select(entry => entry.Path));
        Assert.AreEqual(EntryKind.DirectoryPlaceholder, root[0].EntryKind);

        var docs = catalogue.ListDirectory(snapshot.SnapshotId.Span, "docs");
        SequenceAssert.AreEqual(["docs/big.bin", "docs/small.txt"], docs.Select(entry => entry.Path));

        // Path lookup joins the file-version columns the next incremental
        // needs, including the scan-time identity.
        var big = catalogue.LookupPath(snapshot.SnapshotId.Span, "docs/big.bin");
        Assert.IsNotNull(big);
        Assert.AreEqual(300_000ul, big!.LogicalLength);
        Assert.IsNotNull(big.ModifiedAt);
        Assert.IsNotNull(big.IdentityDevice);
        Assert.IsNotNull(big.IdentityFileId);

        // Case-insensitive resolution folds through the ADR-0026 §8 key.
        Assert.IsNotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "DOCS/Big.BIN", caseInsensitive: true));
        Assert.IsNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "DOCS/Big.BIN"));
    }

    [TestMethod]
    public async Task Backup_AFileIsDeleted_LeavesTheEarlierSnapshotWholeAndRestorable()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xD1), CancellationToken.None);

        var blobsBefore = Directory.EnumerateFiles(
            Path.Combine(StoreRoot, "blobs"), "*", SearchOption.AllDirectories).Count();

        Assert.IsTrue(source.Remove("docs/small.txt"));

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xD2, now: 1_722_600_060_000), CancellationToken.None);

        var before = Enumerable.Repeat((byte)0xD1, 16).ToArray();
        var after = Enumerable.Repeat((byte)0xD2, 16).ToArray();

        // The deletion is recorded as new snapshot state — the later snapshot
        // simply does not name the path.
        Assert.IsNull(catalogue.LookupPath(after, "docs/small.txt"));
        SequenceAssert.AreEqual(["docs/big.bin"], catalogue.ListDirectory(after, "docs").Select(entry => entry.Path));

        // And the earlier snapshot is untouched by it.
        var deleted = catalogue.LookupPath(before, "docs/small.txt");
        Assert.IsNotNull(deleted);
        SequenceAssert.AreEqual(
            ["docs/big.bin", "docs/small.txt"],
            catalogue.ListDirectory(before, "docs").Select(entry => entry.Path));

        // FR-SNP-002's load-bearing half: deleting a file deletes no older file
        // version and no segment record. Nothing proves that like restoring the
        // deleted file's content from the earlier snapshot, cold, afterwards.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var read = await reader.ReadSegmentAsync(deleted!.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);

        using var restored = new MemoryStream();
        var result = await new RestoreEngine(reader).RestoreFileAsync(
            FileVersionManifestCodec.Decode(read.Plaintext!), restored, CancellationToken.None);

        Assert.IsTrue(result.Success, result.FailureDetail);
        SequenceAssert.AreEqual(Deterministic(500, 5), restored.ToArray());

        // Belt and braces: the store only ever grew.
        Assert.IsTrue(
            Directory.EnumerateFiles(Path.Combine(StoreRoot, "blobs"), "*", SearchOption.AllDirectories).Count()
                >= blobsBefore,
            "a deletion removed something from the store — snapshots are new state, never a delete (FR-SNP-002).");
    }

    [TestMethod]
    public async Task IncrementalBackup_TheFileIsUnchanged_ShortCircuitsWithoutReadingItsContent()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        // Change exactly one file; the others keep identity, size, mtime.
        var changed = source.AddFile("readme.md", Deterministic(2_500, 9));
        changed.Metadata = changed.Metadata with { ModifiedAt = 1_722_700_000_000 };

        source.OpenedPaths.Clear();
        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                    ParentSnapshots = [Enumerable.Repeat((byte)0xA1, 16).ToArray()],
                },
                CancellationToken.None);

        // Only the changed file was opened (NFR-PERF-003).
        SequenceAssert.AreEqual(["readme.md"], source.OpenedPaths.Distinct());

        var byPath = second.Files.ToDictionary(file => file.RelativePath);
        Assert.IsTrue(byPath["docs/big.bin"].Reused);
        Assert.IsTrue(byPath["docs/small.txt"].Reused);
        Assert.IsFalse(byPath["readme.md"].Reused);

        // The reused entries name the FIRST snapshot's file versions.
        var firstByPath = first.Files.ToDictionary(file => file.RelativePath);
        Assert.AreEqual(firstByPath["docs/big.bin"].ObjectId, byPath["docs/big.bin"].ObjectId);

        // Both snapshots list identically-shaped trees in the catalogue.
        var snapshots = catalogue.EnumerateSnapshots();
        Assert.AreEqual(2, snapshots.Count);
        SequenceAssert.AreEqual(
            catalogue.ListDirectory(Enumerable.Repeat((byte)0xA1, 16).ToArray(), "docs").Select(entry => entry.Path),
            catalogue.ListDirectory(Enumerable.Repeat((byte)0xB2, 16).ToArray(), "docs").Select(entry => entry.Path));
    }

    [TestMethod]
    public async Task IncrementalBackup_TheFileChanged_StoresOnlyTheSegmentsTheIndexLacks()
    {
        // One file: 4 × 64 KiB segments. The second version changes only
        // the last segment, so exactly one new data record is written.
        var head = Deterministic(3 * 64 * 1024, 11);
        var source = new FakeFileSystemSource();
        source.AddFile("data.bin", [.. head, .. Deterministic(64 * 1024, 13)]);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var changed = source.AddFile("data.bin", [.. head, .. Deterministic(64 * 1024, 17)]);
        changed.Metadata = changed.Metadata with { ModifiedAt = 1_722_700_000_000 };

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                },
                CancellationToken.None);

        // The file re-archived (mtime changed) — but the three unchanged
        // segments were already located by the index, so the second
        // publication's data blobs carry exactly one segment record.
        var file = Assert.ContainsSingle(second.Files);
        Assert.IsFalse(file.Reused);
        Assert.AreEqual(4, file.Archive!.SegmentReferences.Count);
        Assert.AreEqual(1, second.ContentBlobs.Sum(blob => blob.RecordCount));

        // And the second version still restores byte-identical.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(file.Archive.SegmentReferences, restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(changed.Content, restored.ToArray());
    }

    [TestMethod]
    public async Task IncrementalBackup_TheCatalogueWasRebuilt_AnswersTheSameQueriesAndDisablesTheShortCircuit()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        List<CatalogueSnapshot> liveSnapshots;
        List<string> liveListing;
        using (var live = OpenCatalogue())
        {
            await CreateOrchestrator(store, keys, hierarchy, live)
                .PublishAsync(Job(source, 0xA1), CancellationToken.None);
            liveSnapshots = [.. live.EnumerateSnapshots()];
            liveListing = [.. live.ListDirectory(liveSnapshots[0].SnapshotId.Span, "docs").Select(entry => entry.Path)];
        }

        // The catalogue is deleted — a cache — and rebuilt from the store:
        // index plane via E1, manifest plane via the projector.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(SpoolDirectory, "catalogue.db"));

        using var rebuilt = OpenCatalogue();
        var loader = new IndexLoader(store, Repo, hierarchy);
        await new CatalogueRebuilder(loader).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var report = await CatalogueProjector.ProjectAsync(
            rebuilt, reader, store, Repo, keys, hierarchy, CancellationToken.None);

        Assert.AreEqual(1, report.Snapshots);

        // The same answers as the live catalogue.
        var snapshot = Assert.ContainsSingle(rebuilt.EnumerateSnapshots());
        SequenceAssert.AreEqual(liveSnapshots[0].SnapshotId.ToArray(), snapshot.SnapshotId.ToArray());
        Assert.AreEqual(liveSnapshots[0].RootTree, snapshot.RootTree);
        Assert.AreEqual(liveSnapshots[0].CapturedAt, snapshot.CapturedAt);
        Assert.AreEqual(1, snapshot.SignatureState);
        SequenceAssert.AreEqual(
            liveListing,
            rebuilt.ListDirectory(snapshot.SnapshotId.Span, "docs").Select(entry => entry.Path));

        // Identity is scan-time local fact, never durable (02 §2): gone
        // after rebuild, so the short-circuit re-reads rather than trusts.
        var entry = rebuilt.LookupPath(snapshot.SnapshotId.Span, "docs/big.bin");
        Assert.IsNotNull(entry!.ModifiedAt);
        Assert.IsNull(entry.IdentityDevice);
        Assert.IsNull(entry.IdentityFileId);

        source.OpenedPaths.Clear();
        var second = await CreateOrchestrator(store, keys, hierarchy, rebuilt)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xA1, 16).ToArray(),
                },
                CancellationToken.None);

        // Every file re-read — conservative, never wrong — while segment
        // reuse still holds: the index locates every segment, so no new
        // data records are written at all.
        Assert.AreEqual(3, source.OpenedPaths.Distinct().Count());
        foreach (var file in second.Files)
        {
            Assert.IsFalse(file.Reused);
        }
        Assert.AreEqual(0, second.ContentBlobs.Sum(blob => blob.RecordCount));
    }

    [TestMethod]
    public async Task FileVersion_TheFileChanged_NamesTheVersionItReplaced()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xC1), CancellationToken.None);

        var before = first.Files.Single(file => file.RelativePath == "readme.md").ObjectId;

        // Edited: new content, new mtime, same path and inode.
        var node = source.AddFile("readme.md", Deterministic(2_500, 9));
        node.Metadata = node.Metadata with { ModifiedAt = 1_722_700_000_000 };

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xC2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xC1, 16).ToArray(),
                },
                CancellationToken.None);

        // Ancestry: the manifest names the version it replaced. Without it
        // every version claims to be the first, and a file's history is a
        // set of unrelated objects that happen to share a path.
        var after = second.Files.Single(file => file.RelativePath == "readme.md");
        Assert.AreNotEqual(before, after.ObjectId);

        var manifest = await ReadManifestAsync(store, keys, catalogue, after.ObjectId);
        Assert.AreEqual(before, manifest.ParentVersion);
    }

    [TestMethod]
    public async Task IncrementalBackup_TheFileWasRenamed_KeepsItsAncestryAndReadsNoContent()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/before.bin", Deterministic(4_000, 11), fileId: 4_242);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xD1), CancellationToken.None);
        var before = first.Files.Single(file => file.RelativePath == "docs/before.bin").ObjectId;

        var priorManifest = await ReadManifestAsync(store, keys, catalogue, before);

        // The same file, moved. Same inode, same content, same mtime — only
        // the path changed, which is exactly what a rename is.
        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddFile("archive/after.bin", Deterministic(4_000, 11), fileId: 4_242);
        source.OpenedPaths.Clear();

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xD2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xD1, 16).ToArray(),
                },
                CancellationToken.None);

        var after = second.Files.Single(file => file.RelativePath == "archive/after.bin");

        // Keyed on path, this lookup misses and the renamed file becomes a
        // brand-new file with no history — permanently, because the manifest
        // is immutable. Identity is what makes it the same file.
        var manifest = await ReadManifestAsync(store, keys, catalogue, after.ObjectId);
        Assert.AreEqual(before, manifest.ParentVersion);

        // A new manifest, not the prior object re-emitted: the name changed,
        // and a manifest states its own name.
        Assert.AreNotEqual(before, after.ObjectId);
        SequenceAssert.AreEqual("after.bin"u8.ToArray(), manifest.Name.ToArray());

        // Content is not duplicated — every segment resolves to what the
        // first snapshot already wrote.
        Assert.AreEqual(0, second.ContentBlobs.Sum(blob => blob.RecordCount));

        // And it is not re-read either. The prior manifest is fetched and
        // rewritten under the new name, so a moved file costs one record read
        // rather than the whole file (architecture 06 §4.2). Reading it and
        // then discarding every segment as a duplicate is the cost this path
        // exists to stop paying.
        Assert.IsEmpty(source.OpenedPaths);

        // Everything the rename did not change is inherited verbatim, because
        // it describes bytes nothing re-examined.
        SequenceAssert.AreEqual(priorManifest.WholeFileHash.ToArray(), manifest.WholeFileHash.ToArray());
        Assert.AreEqual(priorManifest.LogicalLength, manifest.LogicalLength);
        SequenceAssert.AreEqual(
            priorManifest.SegmentReferences.Select(reference => reference.ObjectId),
            manifest.SegmentReferences.Select(reference => reference.ObjectId));

        // The catalogue keeps the version's real content facts, so the next
        // incremental can still short-circuit a file that has not changed
        // since before it moved.
        var third = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xD3, now: 1_722_700_000_002) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xD2, 16).ToArray(),
                },
                CancellationToken.None);

        Assert.AreEqual(after.ObjectId, third.Files.Single(file => file.RelativePath == "archive/after.bin").ObjectId);
    }

    [TestMethod]
    public async Task IncrementalBackup_TheFileWasRenamedAfterACatalogueRebuild_StillKeepsItsAncestry()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/before.bin", Deterministic(4_000, 11), fileId: 4_242);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        PublishedFileVersion original;
        using (var live = OpenCatalogue())
        {
            var first = await CreateOrchestrator(store, keys, hierarchy, live)
                .PublishAsync(Job(source, 0xE1), CancellationToken.None);
            original = first.Files.Single(file => file.RelativePath == "docs/before.bin");
        }

        // The catalogue is a cache, and it is gone.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(SpoolDirectory, "catalogue.db"));

        using var rebuilt = OpenCatalogue();
        await new CatalogueRebuilder(new IndexLoader(store, Repo, hierarchy)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            CancellationToken.None);

        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            await CatalogueProjector.ProjectAsync(
                rebuilt, reader, store, Repo, keys, hierarchy, CancellationToken.None);
        }

        // The premise: a rebuild recovers paths but not identities, so the
        // catalogue can no longer say which file an inode belongs to.
        var priorSnapshotId = Enumerable.Repeat((byte)0xE1, 16).ToArray();
        Assert.IsNull(rebuilt.LookupIdentity(priorSnapshotId, original.IdentityDevice!.Value, 4_242));

        // The same file, moved, in exactly that window.
        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddFile("archive/after.bin", Deterministic(4_000, 11), fileId: 4_242);

        var second = await CreateOrchestrator(store, keys, hierarchy, rebuilt)
            .PublishAsync(
                Job(source, 0xE2, now: 1_722_700_000_001) with { PriorSnapshotId = priorSnapshotId },
                CancellationToken.None);

        // The snapshot's own source-identity map answers what the cache no
        // longer can (06 §11). Without it this file would claim to be the
        // first of its line — durably, in an immutable manifest, because a
        // local database happened to be cold.
        var after = second.Files.Single(file => file.RelativePath == "archive/after.bin");
        var manifest = await ReadManifestAsync(store, keys, rebuilt, after.ObjectId);
        Assert.AreEqual(original.ObjectId, manifest.ParentVersion);
    }

    [TestMethod]
    public async Task IncrementalBackup_AFileUntouchedForSeveralSnapshotsThenMoved_StillFindsItsAncestor()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/quiet.bin", Deterministic(3_000, 21), fileId: 7_777);
        source.AddFile("docs/busy.bin", Deterministic(1_000, 22), fileId: 7_778);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        ObjectId original;
        using (var live = OpenCatalogue())
        {
            var first = await CreateOrchestrator(store, keys, hierarchy, live)
                .PublishAsync(Job(source, 0xC5), CancellationToken.None);
            original = first.Files.Single(file => file.RelativePath == "docs/quiet.bin").ObjectId;

            // Three more snapshots in which the quiet file is short-circuited
            // and writes no hint of its own. Its answer has to come from the
            // snapshot that created it, several generations back — the case a
            // per-snapshot map holding only new versions could not answer,
            // and the reason the whole-tree map was complete and expensive.
            for (byte seed = 0xC6; seed <= 0xC8; seed++)
            {
                source.AddFile("docs/busy.bin", Deterministic(1_000, seed), fileId: 7_778)
                    .Metadata = EntryMetadata.Empty with { ModifiedAt = 1_722_600_000_000ul + seed };

                await CreateOrchestrator(store, keys, hierarchy, live).PublishAsync(
                    Job(source, seed, now: 1_722_600_000_000ul + seed) with
                    {
                        PriorSnapshotId = Enumerable.Repeat((byte)(seed - 1), 16).ToArray(),
                    },
                    CancellationToken.None);
            }
        }

        // The cache is lost, so only the store can answer.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(SpoolDirectory, "catalogue.db"));

        using var rebuilt = OpenCatalogue();
        await new CatalogueRebuilder(new IndexLoader(store, Repo, hierarchy)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            CancellationToken.None);

        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            await CatalogueProjector.ProjectAsync(
                rebuilt, reader, store, Repo, keys, hierarchy, CancellationToken.None);
        }

        Assert.IsTrue(source.Remove("docs/quiet.bin"));
        source.AddFile("archive/moved.bin", Deterministic(3_000, 21), fileId: 7_777);

        var final = await CreateOrchestrator(store, keys, hierarchy, rebuilt)
            .PublishAsync(
                Job(source, 0xC9, now: 1_722_600_000_100) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xC8, 16).ToArray(),
                },
                CancellationToken.None);

        var moved = final.Files.Single(file => file.RelativePath == "archive/moved.bin");
        var manifest = await ReadManifestAsync(store, keys, rebuilt, moved.ObjectId);

        Assert.AreEqual(original, manifest.ParentVersion);
    }

    [TestMethod]
    public async Task SourceIdentityHints_DeletedFromTheStore_CostOnlyTheRenameAncestry()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/before.bin", Deterministic(4_000, 11), fileId: 4_242);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        using (var live = OpenCatalogue())
        {
            await CreateOrchestrator(store, keys, hierarchy, live)
                .PublishAsync(Job(source, 0xF1), CancellationToken.None);
        }

        var priorSnapshotId = Enumerable.Repeat((byte)0xF1, 16).ToArray();

        // They were published, and they are only hints: deleting every one
        // must leave a publication that still succeeds.
        var hintKeys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("hints/identity/"), ListOptions.Default, CancellationToken.None))
        {
            hintKeys.Add(entry.Key);
        }

        Assert.IsNotEmpty(hintKeys);
        foreach (var key in hintKeys)
        {
            await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(SpoolDirectory, "catalogue.db"));

        using var rebuilt = OpenCatalogue();
        await new CatalogueRebuilder(new IndexLoader(store, Repo, hierarchy)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            CancellationToken.None);

        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            await CatalogueProjector.ProjectAsync(
                rebuilt, reader, store, Repo, keys, hierarchy, CancellationToken.None);
        }

        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddFile("archive/after.bin", Deterministic(4_000, 11), fileId: 4_242);

        var second = await CreateOrchestrator(store, keys, hierarchy, rebuilt)
            .PublishAsync(
                Job(source, 0xF2, now: 1_722_700_000_001) with { PriorSnapshotId = priorSnapshotId },
                CancellationToken.None);

        var after = second.Files.Single(file => file.RelativePath == "archive/after.bin");
        var manifest = await ReadManifestAsync(store, keys, rebuilt, after.ObjectId);

        // The file captures, and the only thing lost is the link to what it
        // used to be — which is what "advisory" costs when it is missing.
        Assert.IsNull(manifest.ParentVersion);
    }


    [TestMethod]
    public async Task IncrementalBackup_OneFileChanged_RewritesMetadataForItAloneAndNotTheRepository()
    {
        // 1 024 files across 32 directories: enough that "proportional to the
        // repository" and "proportional to the change" are far apart.
        var source = new FakeFileSystemSource();
        for (var directory = 0; directory < 32; directory++)
        {
            for (var file = 0; file < 32; file++)
            {
                source.AddFile($"d{directory:d2}/f{file:d2}.bin", Deterministic(2_000, (byte)(directory * 31 + file)));
            }
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var first = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xB1), CancellationToken.None);

        var afterFirst = Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);

        var changed = source.AddFile("d00/f00.bin", Deterministic(2_100, 99));
        changed.Metadata = changed.Metadata with { ModifiedAt = 1_722_700_000_000 };

        var second = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: 1_722_700_000_001) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray(),
                },
                CancellationToken.None);

        var afterSecond = Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);

        // One file changed, so one file-version manifest is new, and so are
        // the trees on the path to it — its own directory, the root — because
        // a tree names its children by identifier and those identifiers moved.
        // Every other directory encodes to the bytes it encoded to last time,
        // is therefore the same object, and is not written again.
        Assert.AreEqual(4, second.MetadataBlobs.Sum(blob => blob.RecordCount));

        var before = first.MetadataBlobs.Sum(blob => blob.Length);
        var after = second.MetadataBlobs.Sum(blob => blob.Length);

        // The shape NFR-PERF-005 is about: a one-file backup of a 1 024-file
        // tree costs a few per cent of a full one, not most of it. Without
        // manifest reuse this was 18 % and rising with the file count, since
        // every directory in the repository was rewritten every run.
        Assert.IsTrue(
            after * 20 < before,
            $"The second snapshot rewrote {after} bytes of metadata against the first's {before}.");

        // And the whole store, not just the manifest plane — which is the
        // assertion the per-snapshot source-identity map made impossible. It
        // described the whole tree every run, so the bytes a snapshot wrote
        // followed the repository however little had changed; keyed by source
        // key, a hint is written once per version and this measures it
        // (06 §11, Q21).
        var growth = afterSecond - afterFirst;
        Assert.IsTrue(
            growth * 20 < afterFirst,
            $"The second snapshot added {growth} bytes to a {afterFirst}-byte store.");
    }

    [TestMethod]
    public async Task IndexDelta_Published_CarriesADigestOfEveryBlobItCovers()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xE1), CancellationToken.None);

        // Read the delta back out of the store rather than trusting the
        // in-process value: what a replicating participant checks is the
        // published object, and only the published object.
        var deltaBytes = await ReadObjectAsync(
            store, MetadataStoreKeys.IndexDelta(0, published.DeltaId));

        var record = StandaloneRecordFraming.Parse(deltaBytes);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
        Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));

        var decoded = IndexDeltaCodec.Decode(plaintext);
        var delta = decoded.Delta;

        Assert.IsNotEmpty(delta.CoveredBlobIds);
        Assert.AreEqual(delta.CoveredBlobIds.Count, delta.CoveredBlobDigests.Count);

        // The signature covers the digests, so what the receiving participant
        // checks against is what the writer asserted (07 §2, §2.2).
        using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
        {
            Assert.IsTrue(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
        }

        // And each digest is the digest of the blob it names, over the bytes
        // the store actually holds, excluding the 16-byte locator (05 §5).
        var byId = published.ContentBlobs.Concat(published.MetadataBlobs)
            .ToDictionary(blob => blob.BlobId, blob => blob.StoreKey);

        for (var i = 0; i < delta.CoveredBlobIds.Count; i++)
        {
            var bytes = await ReadObjectAsync(store, byId[delta.CoveredBlobIds[i]]);
            var expected = System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, bytes.Length - 16));

            SequenceAssert.AreEqual(expected, delta.CoveredBlobDigests[i].ToArray());
        }
    }

    private static async Task<byte[]> ReadObjectAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        using var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome);

        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer, CancellationToken.None);
        return buffer.ToArray();
    }

    /// <summary>Reads a published file-version manifest back out of the store.</summary>
    private static async Task<FileVersionManifest> ReadManifestAsync(
        IObjectStore store, RepositoryKeySet keys, CatalogueDb catalogue, ObjectId objectId)
    {
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }
}
