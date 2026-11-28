using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A blob's record table, which index-delta publication projects every entry
/// from (specification 07 §10), reads back exactly as the blob's own footer
/// declares it, whether the session keeps it in memory or in its spool
/// (ADR-0201); and a session's spool holds nothing of it once the session
/// has ended. FR-MAN-008's deltas are cut from these tables.
/// </summary>
[TestClass]
public sealed class SpilledRecordTableTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task WhileASessionRuns_EachBlobsRecordTableIsWhatItsFooterDeclares()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        var session = archiver.OpenSession();
        await using (session)
        {
            using var source = new MemoryStream(BuildTestFile());
            _ = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);

            Assert.IsGreaterThan(1, session.Blobs.Count, "the file should span several blobs");
            await AssertTablesMatchFootersAsync(store, keys, session.Blobs);
        }
    }

    [TestMethod]
    public async Task OnceASessionEnds_ItsSpoolHoldsNothingOfItsRecordTables()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        var session = archiver.OpenSession();
        await using (session)
        {
            using var source = new MemoryStream(BuildTestFile());
            _ = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);
        }

        Assert.IsEmpty(
            Directory.Exists(SpoolDirectory) ? Directory.GetFiles(SpoolDirectory) : [],
            "a session that finished leaves nothing in the spool, no blob to resume and no table");
    }

    [TestMethod]
    public async Task OnceASessionEnds_ItsBlobsTablesSayToReadTheFooter()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        IReadOnlyList<ArchivedBlob> blobs;
        var session = archiver.OpenSession();
        await using (session)
        {
            using var source = new MemoryStream(BuildTestFile());
            _ = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);
            blobs = session.Blobs;
        }

        Assert.IsNotEmpty(blobs);
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => blobs[0].RecordTable.ToArray());
        Assert.Contains("footer", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual(blobs[0].RecordCount, blobs[0].RecordTable.Count, "the count is the blob's, not the spill's");
    }

    [TestMethod]
    public async Task ASpillACrashedSessionLeft_IsRemovedByTheNextSessionThatSpills()
    {
        // What a session that died mid-run leaves: its tables, on disk, held
        // by nothing. Nothing will read them again.
        Directory.CreateDirectory(SpoolDirectory);
        var left = Path.Combine(SpoolDirectory, $"record-tables-{Guid.NewGuid():N}.spill");
        await File.WriteAllBytesAsync(left, new byte[4096]);

        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);
        var session = archiver.OpenSession();
        await using (session)
        {
            using var source = new MemoryStream(BuildTestFile());
            _ = await session.ArchiveFileAsync(source, priorVersion: null, CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);

            Assert.IsFalse(File.Exists(left), "the next session that spilled should have removed the crashed one's tables");
        }
    }

    [TestMethod]
    public async Task ASingleFileArchive_KeepsItsBlobsRecordTablesAfterItsSessionHasEnded()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var source = new MemoryStream(BuildTestFile());
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        Assert.IsGreaterThan(1, archived.Blobs.Count, "the file should span several blobs");
        await AssertTablesMatchFootersAsync(store, keys, archived.Blobs);
    }

    private static async Task AssertTablesMatchFootersAsync(
        LocalFileSystemObjectStore store, RepositoryKeySet keys, IReadOnlyList<ArchivedBlob> blobs)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var footers = reader.Blobs.ToDictionary(blob => blob.BlobId, blob => blob.Records);

        foreach (var blob in blobs)
        {
            IReadOnlyList<RecordTableEntry> declared = footers[blob.BlobId];
            Assert.AreEqual(declared.Count, blob.RecordCount);
            CollectionAssert.AreEqual(declared.ToArray(), blob.RecordTable.ToArray(), $"blob {blob.BlobId}");
        }
    }
}
