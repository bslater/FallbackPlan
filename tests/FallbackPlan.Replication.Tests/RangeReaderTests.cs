using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// One reader now answers "these exact bytes, or nothing" for the source, the
/// local-path replica, and the peer responder alike (specification peer-protocol
/// 04 §4.2). Because the two ends of a challenge compare answers from it, they
/// must not be able to disagree about which inabilities are honest — so every
/// shape the store contract allows is pinned here.
/// </summary>
[TestClass]
public sealed class RangeReaderTests
{
    private const string Key = "blobs/data/ab/object";

    private string _root = null!;
    private byte[] _payload = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-range-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _payload = new byte[512];
        Random.Shared.NextBytes(_payload);
        await new LocalFileSystemObjectStore(_root).PutAsync(
            ObjectKey.Parse(Key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(_payload, writable: false)),
            PutConditions.None,
            CancellationToken.None);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task Read_AnInboundsRange_AnswersExactlyThoseBytes()
    {
        var store = new LocalFileSystemObjectStore(_root);

        var bytes = await RangeReader.ReadAsync(store, Key, offset: 100, length: 64, CancellationToken.None);

        Assert.IsNotNull(bytes);
        CollectionAssert.AreEqual(_payload[100..164], bytes);
    }

    [TestMethod]
    public async Task Read_AnUnknownKey_AnswersNull()
    {
        var store = new LocalFileSystemObjectStore(_root);

        Assert.IsNull(await RangeReader.ReadAsync(
            store, "blobs/data/cd/absent", offset: 0, length: 16, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_ARangeRunningPastTheEnd_AnswersNull()
    {
        // Half an answer is no answer: a caller that accepted a short read
        // would compare fewer bytes than it challenged.
        var store = new LocalFileSystemObjectStore(_root);

        Assert.IsNull(await RangeReader.ReadAsync(
            store, Key, offset: 480, length: 128, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_AKeyTheStoreCannotEvenParse_AnswersNullRatherThanThrowing()
    {
        // The key arrives off the wire in the responder's case, so a malformed
        // one must not become an exception the session has to survive.
        var store = new LocalFileSystemObjectStore(_root);

        Assert.IsNull(await RangeReader.ReadAsync(
            store, "../escape", offset: 0, length: 16, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_TheStoreFaults_AnswersNullRatherThanPropagating()
    {
        var store = new ReadFaultingObjectStore(new LocalFileSystemObjectStore(_root));
        store.Arm();

        Assert.IsNull(await RangeReader.ReadAsync(store, Key, offset: 0, length: 16, CancellationToken.None));
    }
}
