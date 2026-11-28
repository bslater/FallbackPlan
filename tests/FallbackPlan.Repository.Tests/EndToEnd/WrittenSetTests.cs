namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A session writes each record once, however many of its files carry the
/// content (FR-ARCH-004: reused by reference rather than re-stored; 09 §6).
/// Once a record's blob is uploaded the session keeps no claim to it in
/// memory, only in its spool (ADR-0201), and a later file with the same
/// content still reuses it rather than writing it a second time.
/// </summary>
[TestClass]
public sealed class WrittenSetTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task ADuplicateOfARecordWhoseBlobIsUploaded_IsReusedNotWrittenAgain()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);
        var original = BuildTestFile(seed: 11);

        var session = archiver.OpenSession();
        await using (session)
        {
            ArchiveResult first;
            using (var source = new MemoryStream(original))
            {
                first = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            }

            // Another file behind it seals the blob the first one's last
            // records went into, so every one of them reaches an uploaded blob.
            using (var filler = new MemoryStream(BuildTestFile(seed: 12)))
            {
                _ = await session.ArchiveFileAsync(filler, priorVersion: null, CancellationToken.None);
            }

            var wanted = first.SegmentReferences.Select(reference => reference.ObjectId).ToHashSet();
            await UntilAsync(() => wanted.IsSubsetOf(session.Blobs.SelectMany(blob => blob.RecordTable).Select(record => record.ObjectId)));

            ArchiveResult again;
            using (var source = new MemoryStream(original))
            {
                again = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            }

            await session.FlushAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                first.SegmentReferences.ToArray(), again.SegmentReferences.ToArray(), "the same bytes, the same references");
            var written = session.Blobs.SelectMany(blob => blob.RecordTable).Select(record => record.ObjectId).ToList();
            Assert.HasCount(written.Distinct().Count(), written, "a record the session already holds was written a second time");
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the session's blobs never finished uploading");
            await Task.Delay(10);
        }
    }
}
