using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The local store's records actually arrive (ADR-0043; event ids 2500–2549).
/// </summary>
/// <remarks>
/// <para>
/// The store is where a backup meets the disk, so it is where "the backup just
/// stopped" is usually answered from. Every other suite here builds the store
/// without a logger, which means its four declared messages had call sites
/// that nothing ever reached — the same gap ADR-0043 records at the level of
/// declarations, one layer further out. Declaring a message, calling it, and
/// proving the record arrives are three separate acts.
/// </para>
/// <para>
/// Assertions are by <b>event id</b>, not message text. An id is the thing an
/// operator correlates on and the thing the range allocation pins; a message
/// is prose and may be reworded. A test that matched on prose would fail for
/// an improvement and pass for a deletion.
/// </para>
/// </remarks>
[TestClass]
public sealed class LocalStoreLoggingTests : IDisposable
{
    private const int ObjectPut = 2500;
    private const int ObjectRead = 2501;
    private const int OperationFailed = 2502;
    private const int ObjectDeleted = 2503;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-store-logging-tests", Guid.NewGuid().ToString("n"));

    private readonly RecordingLogger _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalFileSystemObjectStore Store() => new(_root, _log);

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private IEnumerable<RecordedLog> WithId(int eventId) =>
        _log.Records.Where(record => record.EventId == eventId);

    [TestMethod]
    public async Task Store_AnObjectIsPut_RecordsItWithItsSize()
    {
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/written");

        await store.PutAsync(key, Content(new byte[4096]), PutConditions.None, CancellationToken.None);

        var record = WithId(ObjectPut).Single();
        Assert.Contains(
            4096L,
            record.Values.Select(pair => pair.Value),
            "The byte count is the half of this record that answers 'is it still making progress?'");
    }

    [TestMethod]
    public async Task Store_AWholeObjectIsRead_RecordsItAsWhole()
    {
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/whole");
        await store.PutAsync(key, Content(new byte[512]), PutConditions.None, CancellationToken.None);

        using var read = await store.OpenReadAsync(key, range: null, CancellationToken.None);

        var record = WithId(ObjectRead).Single();
        Assert.Contains("whole", record.Values.Select(pair => pair.Value?.ToString()));
    }

    [TestMethod]
    public async Task Store_ARangeIsRead_RecordsItAsRangedRatherThanWhole()
    {
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/ranged");
        await store.PutAsync(key, Content(new byte[512]), PutConditions.None, CancellationToken.None);
        _log.Clear();

        using var read = await store.OpenReadAsync(key, new ObjectRange(16, 32), CancellationToken.None);

        // Whole and ranged share an event id and are told apart by the hole,
        // so a reader chasing a partial-read bug can tell which happened.
        var record = WithId(ObjectRead).Single();
        Assert.Contains("ranged", record.Values.Select(pair => pair.Value?.ToString()));
    }

    [TestMethod]
    public async Task Store_ARangeRunsPastTheEnd_RecordsAWarningNamingTheOperation()
    {
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/short");
        await store.PutAsync(key, Content(new byte[10]), PutConditions.None, CancellationToken.None);
        _log.Clear();

        using var read = await store.OpenReadAsync(key, new ObjectRange(5, 100), CancellationToken.None);

        Assert.AreEqual(OpenReadOutcome.RangeNotSatisfiable, read.Outcome);

        // The refusal is a typed result rather than an exception, which is
        // right — and is exactly why it has to be in the log, or it leaves no
        // trace at all.
        var record = WithId(OperationFailed).Single();
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, record.Level);
        Assert.Contains("get", record.Values.Select(pair => pair.Value?.ToString()));
    }

    [TestMethod]
    public async Task Store_AnObjectIsDeleted_RecordsIt()
    {
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/doomed");
        await store.PutAsync(key, Content(new byte[8]), PutConditions.None, CancellationToken.None);
        _log.Clear();

        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        Assert.ContainsSingle(WithId(ObjectDeleted));
    }

    [TestMethod]
    public async Task Store_EveryRecord_CarriesAnIdInsideTheAllocatedRange()
    {
        // ADR-0043 §3 gives Storage.Local 2500–2599. An id outside it collides
        // with another assembly's, and the collision is only visible in a
        // merged feed — which is the one place nobody is looking.
        var store = Store();
        var key = ObjectKey.Parse("blobs/data/abcd/ranged");
        await store.PutAsync(key, Content(new byte[64]), PutConditions.None, CancellationToken.None);
        using (await store.OpenReadAsync(key, range: null, CancellationToken.None))
        {
        }

        await store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);

        Assert.IsNotEmpty(_log.Records);
        foreach (var record in _log.Records)
        {
            Assert.IsTrue(
                record.EventId is >= 2500 and <= 2599,
                $"event id {record.EventId} is outside Storage.Local's allocated range");
        }
    }
}
