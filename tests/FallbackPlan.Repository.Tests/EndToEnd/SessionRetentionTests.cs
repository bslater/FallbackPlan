using System.Buffers.Binary;
using System.Globalization;
using System.Runtime;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What a snapshot's session keeps in memory for each record it stores
/// (NFR-PERF-001: memory bounded by configuration, not by file count). A
/// session runs for the whole snapshot, so whatever it holds per record grows
/// with everything the snapshot stores; this holds what that is, over a
/// hundred thousand small files, one record each.
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
