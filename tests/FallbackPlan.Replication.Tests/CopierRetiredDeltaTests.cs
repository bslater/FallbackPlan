using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// A copy from a store that retires a delta while the copy runs loses nothing
/// (FR-MAN-017; specification 07 §7). A delta is deleted only once a
/// checkpoint carrying it is durable at the same store, and the copier lists
/// the checkpoint phase after the delta phase, so a delta found gone between
/// its listing and its read is skipped and its carrier is copied in its own
/// phase. Any other object that goes missing under a copy still fails it: a
/// store that loses a blob or a journal record is damage, not retirement.
/// </summary>
[TestClass]
public sealed class CopierRetiredDeltaTests
{
    private static readonly ObjectKey Retired = ObjectKey.Parse("index/delta/0000000000000000/ugq2dinbugq2dinbugq2dinbue");
    private static readonly ObjectKey Kept = ObjectKey.Parse("index/delta/0000000000000000/wkzlfmvswkzlfmvswkzlfmvswi");
    private static readonly ObjectKey Carrier = ObjectKey.Parse("index/checkpoint/0000000000000000/ytb3hq5yytb3hq5yytb3hq5yyq");
    private static readonly ObjectKey Journal = ObjectKey.Parse("journal/2tknjvgu2tknjvgu2tknjvgu2q/0000000000000001");

    private string _root = null!;

    [TestInitialize]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-retired-{Guid.NewGuid():N}");

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
    public async Task ACopy_ADeltaRetiredUnderIt_IsSkipped_AndTheCheckpointCarryingItIsCopied()
    {
        var (_, racing, replica) = await RacingRetirementAsync();

        var outcome = await StoreToStoreCopier.CopyAsync(racing, replica, CancellationToken.None);

        Assert.IsTrue(racing.Fired);
        await AssertTheReplicaHoldsTheCarrierAndNotTheRetiredDeltaAsync(replica);
        Assert.AreEqual(2L, outcome.Copied, "The kept delta and the carrier; the retired delta is not counted as copied.");
    }

    [TestMethod]
    public async Task AConverge_ADeltaRetiredUnderIt_IsSkipped_AndTheCheckpointCarryingItIsCopied()
    {
        var (_, racing, replica) = await RacingRetirementAsync();

        _ = await StoreToStoreCopier.ConvergeAsync(racing, replica, keeps: _ => true, CancellationToken.None);

        Assert.IsTrue(racing.Fired);
        await AssertTheReplicaHoldsTheCarrierAndNotTheRetiredDeltaAsync(replica);
    }

    [TestMethod]
    public async Task ACopy_AnyOtherObjectThatVanishesUnderIt_StillFailsIt()
    {
        var source = new LocalFileSystemObjectStore(Path.Combine(_root, "source"));
        await PutAsync(source, Journal);
        var racing = new InterleavingOnReadObjectStore(source, Journal, async () =>
            _ = await source.DeleteAsync(Journal, DeleteConditions.None, CancellationToken.None));

        _ = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await StoreToStoreCopier.CopyAsync(
                racing, new LocalFileSystemObjectStore(Path.Combine(_root, "replica")), CancellationToken.None));
    }

    // The checkpoint lands, and the delta goes, after the delta phase has
    // listed it and before the copy reads it.
    private async Task<(LocalFileSystemObjectStore Source, InterleavingOnReadObjectStore Racing, LocalFileSystemObjectStore Replica)>
        RacingRetirementAsync()
    {
        var source = new LocalFileSystemObjectStore(Path.Combine(_root, "source"));
        await PutAsync(source, Retired);
        await PutAsync(source, Kept);
        var racing = new InterleavingOnReadObjectStore(source, Retired, async () =>
        {
            await PutAsync(source, Carrier);
            _ = await source.DeleteAsync(Retired, DeleteConditions.None, CancellationToken.None);
        });

        return (source, racing, new LocalFileSystemObjectStore(Path.Combine(_root, "replica")));
    }

    private static async Task AssertTheReplicaHoldsTheCarrierAndNotTheRetiredDeltaAsync(LocalFileSystemObjectStore replica)
    {
        Assert.IsFalse((await replica.GetMetadataAsync(Retired, CancellationToken.None)).Found);
        Assert.IsTrue((await replica.GetMetadataAsync(Kept, CancellationToken.None)).Found);
        Assert.IsTrue(
            (await replica.GetMetadataAsync(Carrier, CancellationToken.None)).Found,
            "The checkpoint phase lists after the delta phase, so the carrier is copied.");
    }

    private static async Task PutAsync(LocalFileSystemObjectStore store, ObjectKey key) =>
        Assert.AreEqual(
            PutOutcome.Created,
            (await store.PutAsync(
                key, _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)),
                PutConditions.IfNotExists, CancellationToken.None)).Outcome);
}
