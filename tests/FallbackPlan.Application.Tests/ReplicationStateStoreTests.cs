using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The per-destination replication ledger (FR-REP-001): it records the last
/// replication of a scope to a destination, keeps one row per pair, and — like
/// the job journal it sits beside — survives reload and is set aside rather than
/// trusted when corrupt.
/// </summary>
[TestClass]
public sealed class ReplicationStateStoreTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-replication-state", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public void Record_ThenReopen_RemembersTheLatest()
    {
        ReplicationStateStore.Open(_state).Record("fp-a", "all", 12, 1_000);

        var record = ReplicationStateStore.Open(_state).Find("fp-a", "all");
        Assert.IsNotNull(record);
        Assert.AreEqual(12, record.Objects);
        Assert.AreEqual(1_000UL, record.ReplicatedAt);
    }

    [TestMethod]
    public void Record_Twice_KeepsOneRowPerDestinationAndScope()
    {
        var store = ReplicationStateStore.Open(_state);
        store.Record("fp-a", "all", 12, 1_000);
        store.Record("fp-a", "all", 3, 2_000);

        Assert.AreEqual(1, store.Records.Count);
        Assert.AreEqual(3, store.Find("fp-a", "all")!.Objects);
        Assert.AreEqual(2_000UL, store.Find("fp-a", "all")!.ReplicatedAt);
    }

    [TestMethod]
    public void Record_DifferentDestinations_AreKeptApart()
    {
        var store = ReplicationStateStore.Open(_state);
        store.Record("fp-a", "all", 1, 1_000);
        store.Record("fp-b", "all", 2, 1_000);

        Assert.AreEqual(2, store.Records.Count);
        Assert.AreEqual(1, store.Find("fp-a", "all")!.Objects);
        Assert.AreEqual(2, store.Find("fp-b", "all")!.Objects);
    }

    [TestMethod]
    public void Open_ACorruptLedger_StartsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_state);
        File.WriteAllText(Path.Combine(_state, "replication.json"), "{ this is not json");

        var store = ReplicationStateStore.Open(_state);
        Assert.AreEqual(0, store.Records.Count);
        Assert.IsTrue(File.Exists(Path.Combine(_state, "replication.json.corrupt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
