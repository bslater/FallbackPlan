using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A backup's index entries fit the deltas a reader can open (FR-MAN-008;
/// specification 07 §2, 00 §8). A delta is a standalone metadata object, and
/// a reader refuses one over 16 MiB before it reads a byte of it. So a
/// backup whose entries would not fit one delta publishes several, cut at
/// blob boundaries under the budget compaction already keeps: each blob is
/// covered by exactly one delta, every record is located by one of them, and
/// the live catalogue applies them all.
/// </summary>
/// <remarks>
/// A backup used to put every record it wrote into one delta. Past about
/// 245 000 records that delta was written without complaint and refused on
/// every read, so a rebuild, a drill, a restore source or an adoption lost
/// the backup's locations. These tests cut at a few kilobytes rather than
/// sixteen mebibytes, through the orchestrator's budget, so the split is
/// exercised without a quarter of a million files.
/// </remarks>
[TestClass]
public sealed class DeltaSplitPublicationTests : ArchiveTestHarness
{
    private const int KiB = 1024;

    private const int SmallBudget = 4 * KiB;

    private const ulong Now = 1_722_600_000_000;

    [TestMethod]
    public void ThePublicationBudget_IsTheMetadataLimitLessTheHeaderMargin()
    {
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        Assert.AreEqual(
            FormatLimits.MaxMetadataObjectSize - IndexDeltaBudget.HeaderMargin, IndexDeltaBudget.Default);
        Assert.AreEqual(
            IndexDeltaBudget.Default,
            Orchestrator(CreateStore(), keys, credential, Sequence("default"), catalogue: null).DeltaByteBudget);
    }

    [TestMethod]
    public async Task ATreeBackup_OverOneDeltasBudget_PublishesSeveralDeltas_EachWithinIt()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "tree.db"), Repo);

        var published = await Orchestrator(store, keys, credential, Sequence("tree"), catalogue, SmallBudget)
            .PublishAsync(Job(Tree(files: 20, seed: 300), 0xA1), CancellationToken.None);

        var deltas = await OwnDeltasAsync(store, credential);
        Assert.IsGreaterThan(1, deltas.Count, "Twenty files' entries fit one four-kilobyte delta only if nothing split them.");
        foreach (var delta in deltas)
        {
            Assert.IsLessThanOrEqualTo(SmallBudget, delta.SignedBytes.Length);
        }

        // In publication order, and every one of them named by the result.
        SequenceAssert.AreEqual(
            deltas.Select(delta => delta.Delta.Sequence).Order(), deltas.Select(delta => delta.Delta.Sequence));
        Assert.HasCount(deltas.Count, published.DeltaIds);

        AssertEveryBlobCoveredOnce([.. published.ContentBlobs, .. published.MetadataBlobs], deltas);

        // The live catalogue applied every delta, not just the last.
        foreach (var record in published.ContentBlobs.Concat(published.MetadataBlobs).SelectMany(blob => blob.RecordTable))
        {
            Assert.IsNotNull(catalogue.ResolveLocation(record.ObjectId), $"Object {record.ObjectId} is not located.");
        }
    }

    [TestMethod]
    public async Task AStreamBackup_OverOneDeltasBudget_PublishesSeveralDeltas_EachWithinIt()
    {
        // The single-stream path builds its delta on its own, so it is held
        // to the same cut separately.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        using var source = new MemoryStream(Incompressible(700, 3 * 1024 * KiB));
        var published = await Orchestrator(store, keys, credential, Sequence("stream"), catalogue: null, SmallBudget)
            .PublishAsync(Job(source, 0xE1), CancellationToken.None);

        var deltas = await OwnDeltasAsync(store, credential);
        Assert.IsGreaterThan(1, deltas.Count, "A three-mebibyte file's entries fit one four-kilobyte delta only if nothing split them.");
        foreach (var delta in deltas)
        {
            Assert.IsLessThanOrEqualTo(SmallBudget, delta.SignedBytes.Length);
        }

        Assert.HasCount(deltas.Count, published.DeltaIds);
        AssertEveryBlobCoveredOnce([.. published.Archive.Blobs, .. published.MetadataBlobs], deltas);
    }

    [TestMethod]
    public async Task ABackupWithinOneDeltasBudget_StillPublishesOneDelta()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        var published = await Orchestrator(store, keys, credential, Sequence("one"), catalogue: null)
            .PublishAsync(Job(Tree(files: 3, seed: 400), 0xB1), CancellationToken.None);

        Assert.ContainsSingle(await OwnDeltasAsync(store, credential));
        Assert.ContainsSingle(published.DeltaIds);
    }

    /// <summary>
    /// Every blob the backup wrote is covered by exactly one delta, with its
    /// digest and root beside it, and every record it holds is an entry of
    /// that delta. A split anywhere but a blob boundary would cover a blob
    /// twice or leave its entries apart from its digest.
    /// </summary>
    private static void AssertEveryBlobCoveredOnce(IReadOnlyList<ArchivedBlob> blobs, IReadOnlyList<DecodedDelta> deltas)
    {
        var coveredBy = new Dictionary<BlobId, IndexDelta>();
        foreach (var delta in deltas.Select(decoded => decoded.Delta))
        {
            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobDigests);
            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobMerkleRoots);
            foreach (var blobId in delta.CoveredBlobIds)
            {
                Assert.IsTrue(coveredBy.TryAdd(blobId, delta), $"Blob {blobId} is covered by more than one delta.");
            }
        }

        Assert.HasCount(blobs.Count, coveredBy);
        foreach (var blob in blobs)
        {
            var delta = coveredBy[blob.BlobId];
            var entries = delta.Entries.Where(entry => entry.BlobId == blob.BlobId).Select(entry => entry.ObjectId).ToHashSet();
            Assert.IsTrue(
                entries.SetEquals(blob.RecordTable.Select(record => record.ObjectId)),
                $"Blob {blob.BlobId}'s records are not the entries of the delta that covers it.");
        }
    }

    private static async Task<List<DecodedDelta>> OwnDeltasAsync(
        LocalFileSystemObjectStore store, RepositoryWriteCredential credential)
    {
        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null, blobState: null,
            CancellationToken.None);

        Assert.IsEmpty(state.Findings, string.Join(" | ", state.Findings.Select(finding => finding.Detail)));
        return [.. state.Deltas
            .Where(delta => delta.Delta.WriterId == Writer && !delta.Delta.IsVoid)
            .OrderBy(delta => delta.Delta.Sequence)];
    }

    private static byte[] Incompressible(int seed, int length)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static FakeFileSystemSource Tree(int files, int seed)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"data/{index:d3}.bin", Incompressible(seed + index, 256 * KiB), fileId: (ulong)(50_000 + index));
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = Now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static BackupJob Job(Stream source, byte snapshotSeed) => new(
        source,
        "report.bin"u8.ToArray(),
        Enumerable.Repeat((byte)0x22, 16).ToArray(),
        Enumerable.Repeat((byte)0x33, 16).ToArray(),
        Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        Now, 3_600_000, 5, "tests/1.0");

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"{name}-sequence.txt")));

    private PublicationOrchestrator Orchestrator(
        IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        CatalogueDb? catalogue,
        int? deltaByteBudget = null)
    {
        if (deltaByteBudget is { } budget)
        {
            return new(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store, sequence, SpoolDirectory,
                FormatVersions.IndexGenerations, observer: null, catalogue)
            {
                DeltaByteBudget = budget,
            };
        }

        return new(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store, sequence, SpoolDirectory,
            FormatVersions.IndexGenerations, observer: null, catalogue);
    }
}
