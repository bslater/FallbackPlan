using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Damage has a scope a person can act on (FR-VER-005, specification 04 §7):
/// from the store keys of damaged blobs to the snapshots and paths a restore
/// of which would meet them, over real publications. Every route that fills
/// a catalogue — the capture that writes one, the projection that rebuilds
/// one from a repository, and the forensic scan that rebuilds one from blobs
/// alone — records what each file version and each snapshot needs, so that
/// every object a capture wrote is traced to what needs it.
/// </summary>
/// <remarks>
/// The trace is the reverse of what the manifests say, and the manifests are
/// sealed: without it, naming what a damaged segment belongs to means
/// decrypting every manifest of every snapshot. So the index is kept beside
/// them, as derived data a rebuild reproduces — and the completeness check
/// here is what stops a route from quietly leaving part of it out, which
/// would make damage there look as though nothing needed it.
/// </remarks>
[TestClass]
public sealed class DamageReachTests : ArchiveTestHarness
{
    private static readonly byte[] First = [.. Enumerable.Repeat((byte)0xD1, 16)];
    private static readonly byte[] Second = [.. Enumerable.Repeat((byte)0xD2, 16)];

    private static string Hex(byte[] snapshotId) => Convert.ToHexStringLower(snapshotId);

    private CatalogueDb OpenCatalogue(string name = "catalogue") =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, name + ".db"), Repo);

    private PublicationOrchestrator Orchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue) =>
        new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, FormatVersions.RelocatableRecords, observer: null, catalogue);

    private static SnapshotJob Job(
        FakeFileSystemSource source, byte[] snapshotId, ulong now = 1_722_600_000_000, byte[]? prior = null) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = snapshotId,
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "damage-reach-tests/1.0",
        PriorSnapshotId = prior,
    };

    private static byte[] Content(int length, byte seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Every blob key the store holds under <c>blobs/</c>, as the sweep names them.</summary>
    private List<string> BlobKeys(string plane = "") =>
        [.. Directory.GetFiles(Path.Combine(StoreRoot, "blobs", plane), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(StoreRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(key => !key.Contains("/.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>A source whose tree has a subdirectory and a file carrying an alternate stream.</summary>
    private static FakeFileSystemSource RichSource()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/report.txt", Content(90_000, 1), fileId: 101);
        source.AddFile("docs/notes/minutes.txt", Content(40_000, 2), fileId: 102);
        var tagged = source.AddFile("docs/tagged.txt", Content(20_000, 3), fileId: 103);
        tagged.AlternateStreams["Zone.Identifier"] = "[ZoneTransfer]\nZoneId=3"u8.ToArray();
        return source;
    }

    [TestMethod]
    public async Task EveryBlobACaptureWrote_IsTraced_ToTheSnapshotThatNeedsIt()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await Orchestrator(store, keys, credential, catalogue).PublishAsync(Job(RichSource(), First), CancellationToken.None);

        AssertEveryBlobTraced(catalogue, keys, First);
    }

    [TestMethod]
    public async Task ACatalogueRebuiltFromTheRepository_TracesEveryBlobAsTheCaptureDid()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using (var live = OpenCatalogue("live"))
        {
            await Orchestrator(store, keys, credential, live).PublishAsync(Job(RichSource(), First), CancellationToken.None);
        }

        using var rebuilt = OpenCatalogue("rebuilt");
        await new CatalogueRebuilder(new IndexLoader(store, Repo, credential)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        await CatalogueProjector.ProjectAsync(rebuilt, reader, store, Repo, keys, credential, CancellationToken.None);

        AssertEveryBlobTraced(rebuilt, keys, First);
    }

    [TestMethod]
    public async Task ACatalogueRebuiltForensicallyFromBlobsAlone_TracesEveryBlobAsTheCaptureDid()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using (var live = OpenCatalogue("live"))
        {
            await Orchestrator(store, keys, credential, live).PublishAsync(Job(RichSource(), First), CancellationToken.None);
        }

        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var rebuilt = OpenCatalogue("forensic");
        var report = await rebuilder.RebuildAsync(rebuilt, new ForensicTarget.Everything(), CancellationToken.None);
        Assert.IsTrue(report.TargetSatisfied);

        AssertEveryBlobTraced(rebuilt, keys, First);
    }

    [TestMethod]
    public async Task ADamagedDataBlob_ReachesTheFilesWhoseContentItHolds_InEverySnapshotThatReusesThem()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        var source = new FakeFileSystemSource();
        source.AddFile("docs/kept.txt", Content(60_000, 4), fileId: 201);
        await Orchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        var firstData = BlobKeys("data");

        // The kept file is unchanged and reused; the added one is new content
        // in a blob of its own.
        source.AddFile("docs/added.txt", Content(60_000, 5), fileId: 202);
        await Orchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, Second, now: 1_722_700_000_000, prior: First), CancellationToken.None);
        var secondData = BlobKeys("data").Except(firstData).ToList();
        Assert.IsNotEmpty(secondData, "the added file's content must be in a blob the first capture did not write");

        var kept = DamageScope.Trace(catalogue, keys, firstData);
        CollectionAssert.AreEquivalent(new[] { Hex(First), Hex(Second) }, kept.Snapshots.ToList());
        CollectionAssert.AreEqual(new[] { "docs/kept.txt" }, kept.FileSample.ToList());

        var added = DamageScope.Trace(catalogue, keys, secondData);
        Assert.AreEqual(Hex(Second), Assert.ContainsSingle(added.Snapshots), "the first snapshot never held the added file");
        CollectionAssert.AreEqual(new[] { "docs/added.txt" }, added.FileSample.ToList());
    }

    [TestMethod]
    public async Task ARenamedFile_IsReachedThroughTheContentItInherited()
    {
        // A rename writes a new version and no content: the new version needs
        // the old one's segments, and so does the snapshot that holds it.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        var source = new FakeFileSystemSource();
        source.AddFile("docs/before.bin", Content(50_000, 6), fileId: 301);
        await Orchestrator(store, keys, credential, catalogue).PublishAsync(Job(source, First), CancellationToken.None);
        var data = BlobKeys("data");

        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddFile("archive/after.bin", Content(50_000, 6), fileId: 301);
        var second = await Orchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, Second, now: 1_722_700_000_001, prior: First), CancellationToken.None);
        Assert.AreEqual(0, second.ContentBlobs.Sum(blob => blob.RecordCount), "a rename writes no content");

        var reach = DamageScope.Trace(catalogue, keys, data);

        CollectionAssert.AreEquivalent(new[] { Hex(First), Hex(Second) }, reach.Snapshots.ToList());
        CollectionAssert.AreEqual(new[] { "archive/after.bin", "docs/before.bin" }, reach.FileSample.ToList());
    }

    [TestMethod]
    public async Task AnAlternateStream_IsContentItsFileNeeds()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        var published = await Orchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(RichSource(), First), CancellationToken.None);

        var tagged = published.Files.Single(file => file.RelativePath == "docs/tagged.txt");
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var manifestRead = await reader.ReadSegmentAsync(tagged.ObjectId, CancellationToken.None);
        var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
        var stream = Assert.ContainsSingle(manifest.Metadata.AlternateStreams);

        CollectionAssert.Contains(manifest.ContentObjects().ToList(), stream.ObjectId, "a stream is content its file needs");
        Assert.IsTrue(reader.TryLocateRecord(stream.ObjectId, out var blobKey, out _));

        var reach = DamageScope.Trace(catalogue, keys, [blobKey.Value]);
        CollectionAssert.Contains(reach.FileSample.ToList(), "docs/tagged.txt");
    }

    [TestMethod]
    public async Task ADamagedKeyNamingNoBlobTheCatalogueLocates_IsUntraced_NotIgnored()
    {
        // A blob this catalogue places nothing in could be garbage, or what
        // another writer's snapshot needs; the trace cannot tell, and so must
        // not report the snapshots it cannot rule out as untouched.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();
        await Orchestrator(store, keys, credential, catalogue).PublishAsync(Job(RichSource(), First), CancellationToken.None);

        var reach = DamageScope.Trace(catalogue, keys, ["blobs/data/zzzz/zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"]);

        Assert.AreEqual(1, reach.Untraced);
        Assert.IsFalse(reach.Complete);
    }

    [TestMethod]
    public async Task TheTreeChainWriter_ReportsEveryManifestItAppends_ContinuationsIncluded()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        await using var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, SmallBlobPolicy.BlobWriteProfile, FormatVersions.RelocatableRecords);
        var entries = Enumerable.Range(0, 40)
            .Select(i => new TreeEntry(System.Text.Encoding.UTF8.GetBytes($"file-{i:D3}.txt"), ObjectId.FromBytes(new byte[32]), EntryKind.File))
            .ToList();
        var written = new List<ObjectId>();

        var head = await TreeChainWriter.WriteAsync(
            builder, entries, "/"u8.ToArray(), NameNormalisation.Unknown, EntryMetadata.Empty, CancellationToken.None,
            shardBudget: 256, written: written);

        Assert.IsTrue(written.Count > 1, "a budget this small splits forty entries into a chain");
        Assert.AreEqual(head, written[^1], "the head is appended last");
        Assert.AreEqual(written.Count, written.Distinct().Count());
    }

    /// <summary>
    /// Every blob in the store is traced — nothing in it is an object the
    /// catalogue cannot place — and between them the blobs reach the snapshot.
    /// </summary>
    private void AssertEveryBlobTraced(CatalogueDb catalogue, RepositoryKeySet keys, byte[] snapshotId)
    {
        var all = BlobKeys();
        Assert.IsNotEmpty(all);
        foreach (var key in all)
        {
            var reach = DamageScope.Trace(catalogue, keys, [key]);
            Assert.IsTrue(reach.Complete, $"{key}: {reach.Untraced} object(s) the catalogue cannot place");
            Assert.AreEqual(Hex(snapshotId), Assert.ContainsSingle(reach.Snapshots), key);
        }

        foreach (var key in BlobKeys("data"))
        {
            Assert.IsTrue(DamageScope.Trace(catalogue, keys, [key]).Files > 0, $"{key} holds content no file was traced to");
        }
    }
}
