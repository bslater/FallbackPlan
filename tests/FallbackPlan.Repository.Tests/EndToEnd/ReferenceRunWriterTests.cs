using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The writer of a file too large for one manifest (ADR-0202 decision 3;
/// FR-ARCH-002, FR-ARCH-010, FR-MAN-004). At format 8 a file whose
/// references and holes number more than 262,144 is written as reference
/// runs, each but the last holding exactly 262,144 pieces, under a manifest
/// that lists them and holds none of its own; a file of 262,144 is one
/// manifest; and below format 8 nothing is written in runs. A file written
/// in runs restores byte for byte, the catalogue its publication writes
/// traces its runs and their segments to it, and a rename of it inherits its
/// runs without reading a byte and is traced through them too. Reading a
/// file's runs holds them to its manifest (ReferenceRunReader), so a test
/// that reads them has checked they cover the file; one file is restored.
/// </summary>
[TestClass]
public sealed class ReferenceRunWriterTests : ArchiveTestHarness
{
    private const int Full = ReferenceRunCodec.MaxPieces;

    /// <summary>Blobs as large as the product writes them, so that a run's size is what it meets, not a small blob's.</summary>
    private static CapturePolicy ProductBlobs { get; } = SmallBlobPolicy with { BlobWriteProfile = BlobWriteProfile.LocalDefault };

    /// <summary>A blob sealed after every record, so that the blob a run is in holds nothing else.</summary>
    private static CapturePolicy BlobPerRecord { get; } = SmallBlobPolicy with
    {
        BlobWriteProfile = BlobWriteProfile.LocalDefault with { TargetSizeBytes = 1 },
    };

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, ushort format,
        CapturePolicy policy, CatalogueDb? catalogue = null) =>
        new(
            policy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            format,
            observer: null,
            catalogue);

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
    /// A file of one-byte stretches of data with a one-byte hole after each,
    /// the last stretch's hole left off when <paramref name="holes"/> is one
    /// fewer: a file of hundreds of thousands of pieces from as many bytes.
    /// The data repeats every 251 bytes, so the segments the stretches become
    /// are 251 records referenced over and over.
    /// </summary>
    private static FakeFileSystemSource.Node Pieces(string path, int data, int holes, ulong fileId)
    {
        Assert.IsTrue(holes == data || holes == data - 1, "each stretch of data but perhaps the last has a hole after it");
        var content = new byte[data + holes];
        for (var stretch = 0; stretch < data; stretch++)
        {
            content[stretch * 2] = (byte)(1 + (stretch % 251));
        }

        return new FakeFileSystemSource.Node
        {
            RelativePath = path,
            Content = content,
            Identity = new ScanIdentity(Device: 7, FileId: fileId, LinkCount: 1),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4 },
            SparseExtents = [.. Enumerable.Range(0, holes).Select(hole => new SparseExtent((ulong)((hole * 2) + 1), 1))],
        };
    }

    private static async Task<FileVersionManifest> ReadManifestAsync(RepositoryReader reader, ObjectId objectId)
    {
        var read = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    private static async Task<List<ReferenceRun>> ReadRunsAsync(RepositoryReader reader, FileVersionManifest manifest)
    {
        List<ReferenceRun> runs = [];
        await foreach (var run in ReferenceRunReader.ReadAsync(manifest, reader.ReadReferenceRunAsync, CancellationToken.None))
        {
            runs.Add(run);
        }

        return runs;
    }

    private static async Task<RepositoryReader> OpenReaderAsync(IObjectStore store, RepositoryKeySet keys)
    {
        var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return reader;
    }

    private static async Task AssertRestoresAsync(RepositoryReader reader, FileVersionManifest manifest, byte[] content)
    {
        using var restored = new MemoryStream();
        var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(content, restored.ToArray());
    }

    private static int PiecesOf(ReferenceRun run) => run.SegmentReferences.Count + run.SparseExtents.Count;

    [TestMethod]
    public async Task AtFormat8_AFilePastOneManifestsPieces_IsWrittenInRuns_EachButTheLastFull_AndRestores()
    {
        // 280,000 pieces: one full run and one of 17,856.
        var source = new FakeFileSystemSource();
        var node = source.AddNode(Pieces("huge.bin", data: 140_000, holes: 140_000, fileId: 9_001));
        source.AddFile("small.bin", [1, 2, 3]);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var published = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, ProductBlobs)
            .PublishAsync(Job(source, 0xE1), CancellationToken.None);

        Assert.IsEmpty(published.Failures, string.Join("; ", published.Failures.Select(failure => failure.Detail)));
        using var reader = await OpenReaderAsync(store, keys);
        var files = await FilesOfAsync(reader, published.RootTreeObjectId);
        var manifest = files.Single(file => file.RelativePath == "huge.bin").Manifest;

        Assert.HasCount(2, manifest.ReferenceRuns);
        Assert.IsEmpty(manifest.SegmentReferences, "a manifest listing runs holds no references of its own");
        Assert.IsEmpty(manifest.SparseExtents, "nor holes");
        Assert.AreEqual(280_000ul, manifest.LogicalLength);

        var runs = await ReadRunsAsync(reader, manifest);
        Assert.AreEqual(0ul, runs[0].Start);
        Assert.AreEqual(Full, PiecesOf(runs[0]));
        Assert.AreEqual((ulong)Full, runs[0].Length, "one byte a piece");
        Assert.AreEqual(runs[0].Length, runs[1].Start);
        Assert.AreEqual(280_000 - Full, PiecesOf(runs[1]));
        Assert.AreEqual(manifest.LogicalLength, runs[1].Start + runs[1].Length);

        await AssertRestoresAsync(reader, manifest, node.Content);

        // A file small enough for one manifest is one manifest, as ever.
        var small = files.Single(file => file.RelativePath == "small.bin").Manifest;
        Assert.IsEmpty(small.ReferenceRuns);
        Assert.ContainsSingle(small.SegmentReferences);
    }

    [TestMethod]
    public async Task AtFormat8_AFileOfAsManyPiecesAsOneManifestHolds_IsOneManifest()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(Pieces("edge.bin", data: Full / 2, holes: Full / 2, fileId: 9_002));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var published = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, ProductBlobs)
            .PublishAsync(Job(source, 0xE2), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        using var reader = await OpenReaderAsync(store, keys);
        var manifest = Assert.ContainsSingle(await FilesOfAsync(reader, published.RootTreeObjectId)).Manifest;

        Assert.IsEmpty(manifest.ReferenceRuns);
        Assert.HasCount(Full / 2, manifest.SegmentReferences);
        Assert.HasCount(Full / 2, manifest.SparseExtents);
    }

    [TestMethod]
    public async Task AtFormat8_AFileOfOnePieceMore_IsAFullRunAndARunOfOnePiece()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(Pieces("edge.bin", data: (Full / 2) + 1, holes: Full / 2, fileId: 9_003));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var published = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, ProductBlobs)
            .PublishAsync(Job(source, 0xE3), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        using var reader = await OpenReaderAsync(store, keys);
        var manifest = Assert.ContainsSingle(await FilesOfAsync(reader, published.RootTreeObjectId)).Manifest;

        var runs = await ReadRunsAsync(reader, manifest);
        Assert.HasCount(2, runs);
        Assert.AreEqual(Full, PiecesOf(runs[0]));
        var last = Assert.ContainsSingle(runs[1].SegmentReferences);
        Assert.IsEmpty(runs[1].SparseExtents);
        Assert.AreEqual((long)Full, last.LogicalOffset, "the last byte of data, after the full run's last hole");
    }

    [TestMethod]
    public async Task AtFormat7_AFilePastOneManifestsPieces_IsOneManifest_WhileItFits()
    {
        // Runs are format 8's: below it a file is one manifest, or too large
        // for one, as before (ADR-0202 decision 5).
        var source = new FakeFileSystemSource();
        source.AddNode(Pieces("huge.bin", data: 140_000, holes: 140_000, fileId: 9_004));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var published = await CreateOrchestrator(store, keys, credential, FormatVersions.StandInFailures, ProductBlobs)
            .PublishAsync(Job(source, 0xE4), CancellationToken.None);

        Assert.IsEmpty(published.Failures);
        using var reader = await OpenReaderAsync(store, keys);
        var manifest = Assert.ContainsSingle(await FilesOfAsync(reader, published.RootTreeObjectId)).Manifest;

        Assert.IsEmpty(manifest.ReferenceRuns);
        Assert.HasCount(140_000, manifest.SegmentReferences);
        Assert.IsFalse(
            reader.AllRecords.Any(record => record.ObjectType == ObjectType.ReferenceRun),
            "no run is written below format 8");
    }

    [TestMethod]
    public async Task AtFormat8_ThePublicationsCatalogue_TracesARunAndTheSegmentsItNames_ToTheFile()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(Pieces("huge.bin", data: 140_000, holes: 140_000, fileId: 9_005));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        var published = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, BlobPerRecord, catalogue)
            .PublishAsync(Job(source, 0xE5), CancellationToken.None);

        using var reader = await OpenReaderAsync(store, keys);
        var manifest = Assert.ContainsSingle(await FilesOfAsync(reader, published.RootTreeObjectId)).Manifest;

        Assert.HasCount(2, manifest.ReferenceRuns);
        foreach (var run in manifest.ReferenceRuns)
        {
            var located = catalogue.WinningLocation(run);
            Assert.IsNotNull(located, $"run {run} is located");
            var blob = published.MetadataBlobs.Single(candidate => candidate.BlobId == located.BlobId);
            var reach = DamageScope.Trace(catalogue, keys, [blob.StoreKey.Value]);
            Assert.AreEqual("huge.bin", Assert.ContainsSingle(reach.FileSample), $"the blob run {run} alone is in");
            Assert.IsTrue(reach.Complete);
        }

        var data = DamageScope.Trace(catalogue, keys, [published.ContentBlobs[0].StoreKey.Value]);
        Assert.AreEqual("huge.bin", Assert.ContainsSingle(data.FileSample), "a segment the runs name");
    }

    [TestMethod]
    public async Task AtFormat8_ARenamedFileWrittenInRuns_InheritsItsRunsUnread_AndIsTracedThroughThem()
    {
        var source = new FakeFileSystemSource();
        var node = source.AddNode(Pieces("docs/before.bin", data: 140_000, holes: 140_000, fileId: 9_006));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        var first = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, ProductBlobs, catalogue)
            .PublishAsync(Job(source, 0xE6), CancellationToken.None);
        var before = Assert.ContainsSingle(await FilesOfAsync(store, keys, first)).ObjectId;

        // The same file, moved: same inode, same content, same time.
        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddNode(node with { RelativePath = "archive/after.bin" });
        source.OpenedPaths.Clear();

        var second = await CreateOrchestrator(store, keys, credential, FormatVersions.ReferenceRuns, ProductBlobs, catalogue)
            .PublishAsync(
                Job(source, 0xE7, now: 1_722_700_000_001) with { PriorSnapshotId = Enumerable.Repeat((byte)0xE6, 16).ToArray() },
                CancellationToken.None);
        var after = Assert.ContainsSingle(await FilesOfAsync(store, keys, second)).ObjectId;

        Assert.IsEmpty(source.OpenedPaths, "a rename reads no content");
        using var reader = await OpenReaderAsync(store, keys);
        var prior = await ReadManifestAsync(reader, before);
        var renamed = await ReadManifestAsync(reader, after);
        Assert.HasCount(2, prior.ReferenceRuns);
        Assert.AreEqual(before, renamed.ParentVersion);
        SequenceAssert.AreEqual(prior.ReferenceRuns, renamed.ReferenceRuns);

        // A segment only the runs name is needed by both versions, so damage
        // to it reaches both names.
        var reach = DamageScope.Trace(catalogue, keys, [first.ContentBlobs[0].StoreKey.Value]);
        CollectionAssert.AreEqual(new[] { "archive/after.bin", "docs/before.bin" }, reach.FileSample.ToArray());
        Assert.HasCount(2, reach.Snapshots);
    }
}
