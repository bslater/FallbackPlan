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

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);
        var (held, result) = await PublishTreeAsync(source, store, keys, credential, catalogue);

        // What the published result held is what the snapshot held for its
        // files until it published: the same list, handed on. The publication's
        // last step ran this method's continuation on its own thread, so its
        // frames, with the result in them, stay beneath this one until this
        // method yields and that thread unwinds.
        await Task.Yield();
        for (var attempt = 0; result.IsAlive && attempt < 50; attempt++)
        {
            await Task.Delay(20);
            _ = Settled();
        }

        var after = Settled();
        Assert.IsFalse(result.IsAlive, "the measurement needs the published result to be gone");

        // The files' content is the source's, not the snapshot's, and is in
        // both readings.
        GC.KeepAlive(source);

        const int segments = TreeFiles * SegmentsPerFile;
        var perSegment = (held.Live - after.Live) / (double)segments;
        Assert.IsLessThan(
            BytesPerReferencedSegment,
            perSegment,
            string.Create(CultureInfo.InvariantCulture,
                $"the snapshot kept {perSegment:f1} bytes for each of {segments} segments its files reference (heap {held.Heap} then {after.Heap} bytes, fragmented {held.Fragmented} then {after.Fragmented})"));
    }

    /// <summary>
    /// Publishes the tree and reads the heap while its result is held, in a
    /// method of its own, so that once it returns nothing of the caller's
    /// refers to the result.
    /// </summary>
    private async Task<((long Live, long Heap, long Fragmented) Held, WeakReference Result)> PublishTreeAsync(
        FakeFileSystemSource source, LocalFileSystemObjectStore store, RepositoryKeySet keys,
        RepositoryWriteCredential credential, CatalogueDb catalogue)
    {
        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue);

        var published = await orchestrator.PublishAsync(new SnapshotJob
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

    private static (long Live, long Heap, long Fragmented) Settled()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return (info.HeapSizeBytes - info.FragmentedBytes, info.HeapSizeBytes, info.FragmentedBytes);
    }
}
