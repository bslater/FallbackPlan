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
/// A tree snapshot's files' segment references and content identifiers wait
/// in its session's spool once each file's manifest is written, rather than
/// in memory until the snapshot publishes (ADR-0201; NFR-PERF-001). The
/// catalogue still learns from them what each version is made of, so damage
/// to a content blob reaches every file that needs it (FR-VER-005). Once the
/// session has ended, a file's references say to read its manifest, and its
/// content identifiers say they were never kept anywhere durable.
/// </summary>
[TestClass]
public sealed class SpilledReferenceTests : ArchiveTestHarness
{
    private const int SegmentBytes = 64 * 1024;

    [TestMethod]
    public async Task DamageToATreeSnapshotsContent_ReachesEveryFileThatNeedsIt()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Distinct(4 * SegmentBytes, 1));
        source.AddFile("docs/b.bin", Distinct(3 * SegmentBytes, 2));
        source.AddFile("docs/c.bin", Distinct(5 * SegmentBytes, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source), CancellationToken.None);

        var reach = catalogue.ReachOf([.. published.ContentBlobs.Select(blob => blob.BlobId)], sampleLimit: 10);
        Assert.AreEqual(3, reach.Files, "every file's content is in those blobs");
        Assert.IsTrue(reach.Complete, "every damaged object traced to a version");
    }

    [TestMethod]
    public async Task OnceItsSessionHasEnded_ATreeFilesReferencesSayToReadItsManifest()
    {
        var content = Distinct(4 * SegmentBytes, 7);
        var source = new FakeFileSystemSource();
        source.AddFile("data.bin", content);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        var published = await CreateOrchestrator(store, keys, credential, catalogue: null)
            .PublishAsync(Job(source), CancellationToken.None);
        var archive = Assert.ContainsSingle(published.Files).Archive!;

        // What the file is stays answerable: its length and how many segments.
        Assert.AreEqual(content.Length, archive.LogicalLength);
        Assert.AreEqual(4, archive.SegmentReferences.Count);
        Assert.AreEqual(4, archive.SegmentContentIds.Count);

        var references = Assert.ThrowsExactly<InvalidOperationException>(() => archive.SegmentReferences.ToList());
        Assert.Contains("manifest", references.Message, StringComparison.Ordinal);
        var contentIds = Assert.ThrowsExactly<InvalidOperationException>(() => archive.SegmentContentIds.ToList());
        Assert.Contains("never written anywhere durable", contentIds.Message, StringComparison.Ordinal);

        // And the manifest has them, and they restore the file.
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(
            await ReferencesOfAsync(reader, published.Files[0].ObjectId), restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(content, restored.ToArray());
    }

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb? catalogue) =>
        new(
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
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat((byte)0xC4, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    /// <summary>Content none of whose 64 KiB segments repeats another file's or its own.</summary>
    private static byte[] Distinct(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
