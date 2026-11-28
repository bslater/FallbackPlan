using System.Buffers.Binary;
using System.Globalization;
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
/// The measurement is a forced-collection live set, net of one taken once the
/// session has warmed up, and is process-wide, so the class runs alone.
/// </remarks>
[DoNotParallelize]
[TestClass]
public sealed class SessionRetentionTests : ArchiveTestHarness
{
    private const int WarmUpFiles = 1_000;
    private const int MeasuredFiles = 100_000;
    private const int FileBytes = 512;

    /// <summary>
    /// What a session may keep per record it has stored. A blob's record
    /// table spills to the session's spool once the blob is uploaded
    /// (ADR-0201), so what stays is the claim each record holds in the
    /// session's written set.
    /// </summary>
    private const double BytesPerStoredRecord = 128;

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
            var baseline = SettledLiveSet();

            await ArchiveFilesAsync(session, template, WarmUpFiles, WarmUpFiles + MeasuredFiles);
            var perRecord = (SettledLiveSet() - baseline) / (double)MeasuredFiles;

            Assert.IsLessThan(
                BytesPerStoredRecord,
                perRecord,
                string.Create(CultureInfo.InvariantCulture,
                    $"the session kept {perRecord:f1} bytes for each of {MeasuredFiles} stored records"));
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

    private static long SettledLiveSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
