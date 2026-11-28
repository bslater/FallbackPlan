using System.Buffers.Binary;
using System.Globalization;
using System.Runtime;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What a snapshot keeps in memory for each record it stores and each segment
/// its files reference (NFR-PERF-001: memory bounded by configuration, not by
/// file count). A session runs for the whole snapshot, so whatever it holds per
/// record grows with everything the snapshot stores; this holds what that is,
/// over a hundred thousand small files, one record each. A tree snapshot holds
/// each file it captured until it publishes, and this holds what that costs
/// per segment, over files of a thousand segments each.
/// </summary>
/// <remarks>
/// The measurement is the heap after a compacting collection, less what the
/// collector reports as fragmentation, net of one taken once the session has
/// warmed up. A collection that only sweeps leaves free space between live
/// objects, and how much depends on the platform and the moment, so reading
/// without compacting measured the collector as much as the session. It is
/// process-wide, so the class runs alone.
/// </remarks>
[DoNotParallelize]
[TestClass]
public sealed class SessionRetentionTests : ArchiveTestHarness
{
    private const int WarmUpFiles = 1_000;
    private const int MeasuredFiles = 100_000;
    private const int FileBytes = 512;

    /// <summary>
    /// What a session may keep per record it has stored. Once a record's blob
    /// is uploaded, its entry in the blob's record table and its claim in the
    /// session's written set both wait in the session's spool (ADR-0201), so
    /// what stays is the blob's own identity, shared by every record in it.
    /// </summary>
    private const double BytesPerStoredRecord = 16;

    private const int TreeFiles = 8;
    private const int SegmentsPerFile = 1_024;
    private const int SegmentBytes = 64 * 1024;

    /// <summary>
    /// What a tree snapshot may keep per segment its files reference. Once a
    /// file's manifest is written, its references and content identifiers wait
    /// in the session's spool (ADR-0201), so what stays is the file's own
    /// result, shared by every segment in it. A reference kept in memory is 48
    /// bytes and a content identifier 32, so either alone exceeds this.
    /// </summary>
    private const double BytesPerReferencedSegment = 16;

    /// <summary>
    /// How many times the tree's result may be released before the least
    /// figure stands. Whatever else the process lets go of between a
    /// release's two readings is counted as the result's: a pooled array
    /// trimmed, say, or a retiring thread's caches. That only ever raises a
    /// release's figure, and it comes when it comes, while what the result
    /// keeps it keeps in every release; so the least of them is what it kept.
    /// </summary>
    private const int TreeReleases = 3;

    /// <summary>The large object heap's place in <see cref="GCMemoryInfo.GenerationInfo"/>.</summary>
    private const int LargeObjectHeap = 3;

    [TestMethod]
    public async Task ASession_KeepsLittleInMemoryForEachRecordItHasStored()
    {
        using var keys = CreateKeys();
        var archiver = new FileArchiver(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, new DiscardingObjectStore(),
            new MonotonicBlobCounterAllocator(1), SpoolDirectory, FormatVersions.SealedDataPlane);

        var template = new byte[FileBytes];
        new Random(20261128).NextBytes(template);

        var session = archiver.OpenSession();
        await using (session)
        {
            await ArchiveFilesAsync(session, template, 0, WarmUpFiles);
            var baseline = Settled();

            await ArchiveFilesAsync(session, template, WarmUpFiles, WarmUpFiles + MeasuredFiles);
            var after = Settled();
            var perRecord = (after.Live - baseline.Live) / (double)MeasuredFiles;

            Assert.IsLessThan(
                BytesPerStoredRecord,
                perRecord,
                string.Create(CultureInfo.InvariantCulture,
                    $"the session kept {perRecord:f1} bytes for each of {MeasuredFiles} stored records (heap {baseline.Heap} then {after.Heap} bytes, fragmented {baseline.Fragmented} then {after.Fragmented})"));
        }
    }

    [TestMethod]
    public async Task ATreeSnapshot_KeepsLittleInMemoryForEachSegmentItsFilesReference()
    {
        // Every segment of every file is the same, so the snapshot stores one
        // and references it eight thousand times: the references are what is
        // measured, not what storing costs.
        var content = new byte[SegmentsPerFile * SegmentBytes];
        var source = new FakeFileSystemSource();
        for (var file = 0; file < TreeFiles; file++)
        {
            source.AddFile(string.Create(CultureInfo.InvariantCulture, $"data/{file:d2}.bin"), content);
        }

        const int segments = TreeFiles * SegmentsPerFile;
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        List<string> releases = [];
        var perSegment = double.PositiveInfinity;
        for (var release = 0; release < TreeReleases && perSegment >= BytesPerReferencedSegment; release++)
        {
            // Each release publishes the same tree afresh, into a store and a
            // catalogue of its own, so none reuses what one before it stored.
            var store = new LocalFileSystemObjectStore(
                Path.Combine(StoreRoot, release.ToString(CultureInfo.InvariantCulture)));
            using var catalogue = CatalogueDb.Open(
                Path.Combine(SpoolDirectory, string.Create(CultureInfo.InvariantCulture, $"catalogue-{release}.db")), Repo);
            var (held, result) = await PublishTreeAsync(source, store, keys, credential, catalogue, release);
            for (var attempt = 0; result.IsAlive && attempt < 50; attempt++)
            {
                await Task.Delay(20);
                _ = Settled();
            }

            var after = Settled();
            Assert.IsFalse(result.IsAlive, "the measurement needs the published result to be gone");

            var released = (held.Live - after.Live) / (double)segments;
            perSegment = Math.Min(perSegment, released);
            releases.Add(string.Create(CultureInfo.InvariantCulture,
                $"{released:f1} (heap {held.Heap} then {after.Heap} bytes, large objects {held.LargeObjects} then {after.LargeObjects}, fragmented {held.Fragmented} then {after.Fragmented})"));
        }

        // The files' content is the source's, not the snapshot's, and is in
        // every reading.
        GC.KeepAlive(source);

        Assert.IsLessThan(
            BytesPerReferencedSegment,
            perSegment,
            string.Create(CultureInfo.InvariantCulture,
                $"the snapshot kept at least {perSegment:f1} bytes for each of {segments} segments its files reference, in every release: {string.Join("; ", releases)}"));
    }

    /// <summary>
    /// Publishes the tree and reads the heap while its result is held, in a
    /// method of its own, so that once it returns nothing of the caller's
    /// refers to the result. What the result holds is what the snapshot held
    /// for its files until it published: the same list, handed on.
    /// </summary>
    /// <remarks>
    /// Left to itself, the publication's last step runs the awaiting code on
    /// as part of its own frames, which still refer to what the publication
    /// used (its index entries and source-identity hints among them), so a
    /// reading taken there counts them as the result's. The await yields
    /// instead, and the reading waits for the thread that finished the
    /// publication to unwind.
    /// </remarks>
    private async Task<((long Live, long Heap, long LargeObjects, long Fragmented) Held, WeakReference Result)> PublishTreeAsync(
        FakeFileSystemSource source, LocalFileSystemObjectStore store, RepositoryKeySet keys,
        RepositoryWriteCredential credential, CatalogueDb catalogue, int release)
    {
        var sequence = Path.Combine(
            SpoolDirectory, string.Create(CultureInfo.InvariantCulture, $"sequence-{release}.txt"));
        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(sequence)),
            SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue);

        var publishing = orchestrator.PublishAsync(new SnapshotJob
        {
            Source = source,
            Roots = [new ScanRoot("/")],
            DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
            BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
            SnapshotId = Enumerable.Repeat((byte)0xD5, 16).ToArray(),
            NowUnixMilliseconds = 1_722_600_000_000,
            DeclaredMaxDurationMs = 3_600_000,
            ExpiryGeneration = 5,
            ClientVersion = "fallbackplan-tests/1.0",
        }, CancellationToken.None);
        var published = await publishing.AsTask().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await Task.Delay(100);

        Assert.HasCount(TreeFiles, published.Files);
        Assert.IsTrue(published.Files.All(file => file.Archive!.SegmentReferences.Count == SegmentsPerFile));
        return (Settled(), new WeakReference(published));
    }

    private static async Task ArchiveFilesAsync(ArchiveSession session, byte[] template, int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            // Each file differs from every other in its first four bytes, so
            // each is a record of its own rather than a reuse of the last.
            var content = template.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(content, index);
            using var stream = new MemoryStream(content, writable: false);
            _ = await session.ArchiveFileAsync(stream, priorVersion: null, CancellationToken.None);
        }
    }

    private static (long Live, long Heap, long LargeObjects, long Fragmented) Settled()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return (
            info.HeapSizeBytes - info.FragmentedBytes,
            info.HeapSizeBytes,
            info.GenerationInfo[LargeObjectHeap].SizeAfterBytes,
            info.FragmentedBytes);
    }
}
