using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// A copy holds back a snapshot record its caller says the destination lacks
/// the closure of (FR-DEST-004, ADR-0198 Amendment 3). A record's presence is
/// what says a backup is at a destination, and a copy sends records last so
/// that it can; a caller that finds the blobs did not all arrive asks for the
/// record not to be sent, and everything else is copied as before.
/// </summary>
[TestClass]
public sealed class CopierWithheldRecordTests
{
    private const string Blob = "blobs/data/aaaa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Delta = "index/delta/0000000000000000/ugq2dinbugq2dinbugq2dinbue";
    private const string Older = "snapshots/device-a/set-a/older";
    private const string Newest = "snapshots/device-a/set-a/newest";

    private static readonly string[] Everything = [Blob, Delta, Older, Newest];

    private string _root = null!;

    [TestInitialize]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-withheld-{Guid.NewGuid():N}");

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
    public async Task Copy_SendsEverythingButTheRecordItIsToldToWithhold()
    {
        var (source, replica) = await StoresAsync();
        var held = new List<string>();

        var outcome = await StoreToStoreCopier.CopyAsync(
            source, replica, CancellationToken.None, holds: held.Add, withholds: key => key == Newest);

        Assert.IsFalse(await HoldsAsync(replica, Newest), "the withheld record was sent");
        foreach (var key in new[] { Blob, Delta, Older })
        {
            Assert.IsTrue(await HoldsAsync(replica, key), $"{key} was not sent");
        }

        Assert.DoesNotContain(Newest, held);
        Assert.AreEqual(3L, outcome.Copied);
    }

    [TestMethod]
    public async Task Converge_SendsEverythingButTheRecordItIsToldToWithhold()
    {
        var (source, replica) = await StoresAsync();
        var held = new List<string>();

        var outcome = await StoreToStoreCopier.ConvergeAsync(
            source, replica, _ => true, CancellationToken.None, holds: held.Add, withholds: key => key == Newest);

        Assert.IsFalse(await HoldsAsync(replica, Newest), "the withheld record was sent");
        foreach (var key in new[] { Blob, Delta, Older })
        {
            Assert.IsTrue(await HoldsAsync(replica, key), $"{key} was not sent");
        }

        Assert.DoesNotContain(Newest, held);
        Assert.AreEqual(3L, outcome.Copied);
    }

    [TestMethod]
    public async Task OnlyRecordsAreWithheld_NeverWhatTheyName()
    {
        // A caller that would withhold every key holds back the records alone:
        // the blobs and the index are what a later pass completes the
        // destination from.
        var (source, replica) = await StoresAsync();

        await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None, withholds: _ => true);
        var (convergeSource, convergeReplica) = await StoresAsync("converge");
        await StoreToStoreCopier.ConvergeAsync(
            convergeSource, convergeReplica, _ => true, CancellationToken.None, withholds: _ => true);

        foreach (var store in new[] { replica, convergeReplica })
        {
            Assert.IsTrue(await HoldsAsync(store, Blob), "a blob was withheld");
            Assert.IsTrue(await HoldsAsync(store, Delta), "the index was withheld");
            Assert.IsFalse(await HoldsAsync(store, Older), "a record was sent");
            Assert.IsFalse(await HoldsAsync(store, Newest), "a record was sent");
        }
    }

    [TestMethod]
    public async Task ARecordTheDestinationHoldsAlready_StaysThere()
    {
        var (source, replica) = await StoresAsync();
        await PutAsync(replica, Newest);
        var held = new List<string>();

        await StoreToStoreCopier.CopyAsync(
            source, replica, CancellationToken.None, holds: held.Add, withholds: key => key == Newest);

        Assert.IsTrue(await HoldsAsync(replica, Newest), "a record the destination held was taken away");
        Assert.Contains(Newest, held);
    }

    private async Task<(LocalFileSystemObjectStore Source, LocalFileSystemObjectStore Replica)> StoresAsync(string name = "copy")
    {
        var source = new LocalFileSystemObjectStore(Path.Combine(_root, name, "source"));
        var replica = new LocalFileSystemObjectStore(Path.Combine(_root, name, "replica"));
        foreach (var key in Everything)
        {
            await PutAsync(source, key);
        }

        return (source, replica);
    }

    private static async Task<bool> HoldsAsync(LocalFileSystemObjectStore store, string key) =>
        (await store.GetMetadataAsync(ObjectKey.Parse(key), CancellationToken.None)).Found;

    private static async Task PutAsync(LocalFileSystemObjectStore store, string key)
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        await store.PutAsync(
            ObjectKey.Parse(key),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            PutConditions.None,
            CancellationToken.None);
    }
}
