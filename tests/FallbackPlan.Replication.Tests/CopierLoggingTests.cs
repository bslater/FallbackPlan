using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// A replication pass moves the only copies a person has off this machine, and
/// until now it did so without saying a word. These assert that the pass
/// reports itself — by event id, through a real copy and a real convergence.
/// </summary>
/// <remarks>
/// The counts asserted here are the ones the copier computes rather than the
/// ones the original declarations promised. It never totals the work up front,
/// because it streams from a listing; what it knows at the start is the
/// destination's opening inventory, which is what decides whether a catch-up
/// costs seconds or hours.
/// </remarks>
[TestClass]
public sealed class CopierLoggingTests
{
    private const int ReplicationStarting = 3000;
    private const int ObjectCopied = 3001;
    private const int ConvergeDeleteRefused = 3002;
    private const int ReplicationComplete = 3003;

    private string _root = null!;
    private string _sourcePath = null!;
    private string _replicaPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-log-{Guid.NewGuid():N}");
        _sourcePath = Path.Combine(_root, "source");
        _replicaPath = Path.Combine(_root, "replica");
        Directory.CreateDirectory(_sourcePath);
        Directory.CreateDirectory(_replicaPath);
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
    public async Task Copying_ToANamedDestination_ReportsTheOpeningInventoryAndEveryObject()
    {
        var log = new RecordingLogger();
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        await SeedAsync(source, count: 3);

        var outcome = await StoreToStoreCopier.CopyAsync(
            source, replica, CancellationToken.None, "off-site", log);

        Assert.AreEqual(3L, outcome.Copied);

        var opening = log.Records.Single(record => record.EventId == ReplicationStarting);
        Assert.AreEqual(0, opening.Values.First(value => value.Key == "Held").Value);
        Assert.AreEqual("off-site", opening.Values.First(value => value.Key == "Destination").Value);

        Assert.HasCount(
            3, log.Records.Where(record => record.EventId == ObjectCopied),
            "one record per object copied, at Trace — the level that answers 'what actually crossed'");

        var finished = log.Records.Single(record => record.EventId == ReplicationComplete);
        Assert.AreEqual(3L, finished.Values.First(value => value.Key == "Copied").Value);
    }

    [TestMethod]
    public async Task Copying_ASecondTime_ReportsTheDestinationAlreadyHoldsEverything()
    {
        // The opening inventory is the whole point of the starting record: a
        // second pass copies nothing and should say why before it says how much.
        var source = new LocalFileSystemObjectStore(_sourcePath);
        var replica = new LocalFileSystemObjectStore(_replicaPath);
        await SeedAsync(source, count: 3);
        await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None);

        var log = new RecordingLogger();
        var outcome = await StoreToStoreCopier.CopyAsync(
            source, replica, CancellationToken.None, "off-site", log);

        Assert.AreEqual(0L, outcome.Copied);
        Assert.AreEqual(3L, outcome.AlreadyHeld);

        var opening = log.Records.Single(record => record.EventId == ReplicationStarting);
        Assert.AreEqual(3, opening.Values.First(value => value.Key == "Held").Value);
        Assert.IsEmpty(log.Records.Where(record => record.EventId == ObjectCopied));
    }

    [TestMethod]
    public async Task Converging_WhenTheReplicaWillNotDelete_SaysSoRatherThanCountingItAway()
    {
        // The silence this replaced: a delete the destination declined was
        // counted as not-deleted and mentioned nowhere, leaving a replica
        // holding an object its policy dropped with nothing to explain it.
        var source = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(source, count: 2);
        await StoreToStoreCopier.CopyAsync(
            source, new LocalFileSystemObjectStore(_replicaPath), CancellationToken.None);

        var log = new RecordingLogger();
        var replica = new DeleteFaultingObjectStore(new LocalFileSystemObjectStore(_replicaPath));
        replica.ArmNotFound();

        var outcome = await StoreToStoreCopier.ConvergeAsync(
            source, replica, _ => false, CancellationToken.None, "off-site", log);

        Assert.AreEqual(0L, outcome.Deleted);
        Assert.HasCount(
            2, log.Records.Where(record => record.EventId == ConvergeDeleteRefused),
            "every object the pass condemned and could not remove is named");

        var finished = log.Records.Single(record => record.EventId == ReplicationComplete);
        Assert.AreEqual("converge", finished.Values.First(value => value.Key == "Pass").Value);
    }

    private static async Task SeedAsync(LocalFileSystemObjectStore source, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var payload = new byte[256];
            Random.Shared.NextBytes(payload);
            await source.PutAsync(
                ObjectKey.Parse($"blobs/data/{index:x2}{index:x2}/object-{index}"),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }
    }
}
