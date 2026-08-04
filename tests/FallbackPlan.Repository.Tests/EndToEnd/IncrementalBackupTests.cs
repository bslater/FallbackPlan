using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Incremental backup over catalogue v2 (phase-1 wave T2): the live
/// catalogue learns each publication, an unchanged file short-circuits on
/// identity + size + mtime without its content being read (NFR-PERF-003),
/// a changed file re-archives but stores only the segments the index does
/// not already locate (09 §6), and a rebuilt catalogue answers the same
/// queries — with the short-circuit conservatively disabled because
/// identities are not durable (02 §2).
/// </summary>
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

    [Fact]
    public async Task The_live_catalogue_answers_snapshots_paths_and_listings_after_publication()
    {
        var source = BuildSource();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, hierarchy, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        var snapshot = Assert.Single(catalogue.EnumerateSnapshots());
        Assert.Equal(Enumerable.Repeat((byte)0xA1, 16).ToArray(), snapshot.SnapshotId.ToArray());
        Assert.Equal(1_722_600_000_000ul, snapshot.CapturedAt);
        Assert.Equal(published.RootTreeObjectId, snapshot.RootTree);
        Assert.Equal(1, snapshot.CaptureStatus);
        Assert.Equal(1, snapshot.SignatureState);

        // ls / — byte order: docs, readme.md.
        var root = catalogue.ListDirectory(snapshot.SnapshotId.Span, string.Empty);
        Assert.Equal(["docs", "readme.md"], root.Select(entry => entry.Path));
        Assert.Equal(EntryKind.DirectoryPlaceholder, root[0].EntryKind);

        var docs = catalogue.ListDirectory(snapshot.SnapshotId.Span, "docs");
        Assert.Equal(["docs/big.bin", "docs/small.txt"], docs.Select(entry => entry.Path));

        // Path lookup joins the file-version columns the next incremental
        // needs, including the scan-time identity.
        var big = catalogue.LookupPath(snapshot.SnapshotId.Span, "docs/big.bin");
        Assert.NotNull(big);
        Assert.Equal(300_000ul, big!.LogicalLength);
        Assert.NotNull(big.ModifiedAt);
        Assert.NotNull(big.IdentityDevice);
        Assert.NotNull(big.IdentityFileId);

        // Case-insensitive resolution folds through the ADR-0026 §8 key.
        Assert.NotNull(catalogue.LookupPath(snapshot.SnapshotId.Span, "DOCS/Big.BIN", caseInsensitive: true));
        Assert.Null(catalogue.LookupPath(snapshot.SnapshotId.Span, "DOCS/Big.BIN"));
    }

    [Fact]
    public async Task An_unchanged_file_short_circuits_without_its_content_being_read()
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
        Assert.Equal(["readme.md"], source.OpenedPaths.Distinct());

        var byPath = second.Files.ToDictionary(file => file.RelativePath);
        Assert.True(byPath["docs/big.bin"].Reused);
        Assert.True(byPath["docs/small.txt"].Reused);
        Assert.False(byPath["readme.md"].Reused);

        // The reused entries name the FIRST snapshot's file versions.
        var firstByPath = first.Files.ToDictionary(file => file.RelativePath);
        Assert.Equal(firstByPath["docs/big.bin"].ObjectId, byPath["docs/big.bin"].ObjectId);

        // Both snapshots list identically-shaped trees in the catalogue.
        var snapshots = catalogue.EnumerateSnapshots();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(
            catalogue.ListDirectory(Enumerable.Repeat((byte)0xA1, 16).ToArray(), "docs").Select(entry => entry.Path),
            catalogue.ListDirectory(Enumerable.Repeat((byte)0xB2, 16).ToArray(), "docs").Select(entry => entry.Path));
    }

    [Fact]
    public async Task A_changed_file_stores_only_the_segments_the_index_does_not_already_locate()
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
        var file = Assert.Single(second.Files);
        Assert.False(file.Reused);
        Assert.Equal(4, file.Archive!.SegmentReferences.Count);
        Assert.Equal(1, second.ContentBlobs.Sum(blob => blob.RecordCount));

        // And the second version still restores byte-identical.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(file.Archive.SegmentReferences, restored, CancellationToken.None);
        Assert.True(restore.Success, restore.FailureDetail);
        Assert.Equal(changed.Content, restored.ToArray());
    }

    [Fact]
    public async Task A_rebuilt_catalogue_answers_the_same_queries_and_disables_the_short_circuit()
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

        Assert.Equal(1, report.Snapshots);

        // The same answers as the live catalogue.
        var snapshot = Assert.Single(rebuilt.EnumerateSnapshots());
        Assert.Equal(liveSnapshots[0].SnapshotId.ToArray(), snapshot.SnapshotId.ToArray());
        Assert.Equal(liveSnapshots[0].RootTree, snapshot.RootTree);
        Assert.Equal(liveSnapshots[0].CapturedAt, snapshot.CapturedAt);
        Assert.Equal(1, snapshot.SignatureState);
        Assert.Equal(
            liveListing,
            rebuilt.ListDirectory(snapshot.SnapshotId.Span, "docs").Select(entry => entry.Path));

        // Identity is scan-time local fact, never durable (02 §2): gone
        // after rebuild, so the short-circuit re-reads rather than trusts.
        var entry = rebuilt.LookupPath(snapshot.SnapshotId.Span, "docs/big.bin");
        Assert.NotNull(entry!.ModifiedAt);
        Assert.Null(entry.IdentityDevice);
        Assert.Null(entry.IdentityFileId);

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
        Assert.Equal(3, source.OpenedPaths.Distinct().Count());
        Assert.All(second.Files, file => Assert.False(file.Reused));
        Assert.Equal(0, second.ContentBlobs.Sum(blob => blob.RecordCount));
    }
}
