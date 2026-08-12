using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The replica attribution store (peer-protocol 05 §2): which peer each
/// replica repository belongs to — the quota's denominator, and later the
/// authority a retention command is validated against.
/// </summary>
[TestClass]
public sealed class ReplicaOwnerStoreTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-owner-tests", Guid.NewGuid().ToString("n"));

    private const string RepoA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RepoB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public void TryAttribute_AFreshRepository_AttributesAndRepeatsIdempotently()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one"));

        // The same peer offering the same repository again is the ordinary
        // resumption path, not a conflict.
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one"));
        Assert.ContainsSingle(store.OwnedBy("peer-one"));
    }

    [TestMethod]
    public void TryAttribute_ARepositoryAnotherPeerOwns_IsRefused()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");

        // One household's archive must never count against another's quota
        // (05 §2) — the second peer is refused, and the attribution stands.
        Assert.IsFalse(store.TryAttribute(RepoA, "peer-two"));
        Assert.ContainsSingle(store.OwnedBy("peer-one"));
        Assert.IsEmpty(store.OwnedBy("peer-two"));
    }

    [TestMethod]
    public void Open_AfterAttributionsWereWritten_ReadsThemBack()
    {
        var first = ReplicaOwnerStore.Open(_stateDirectory);
        first.TryAttribute(RepoA, "peer-one");
        first.TryAttribute(RepoB, "peer-one");

        // A fresh open — a restart — still knows the denominator, which is
        // the store's whole reason to exist across sessions.
        var reopened = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.HasCount(2, reopened.OwnedBy("peer-one"));
        Assert.IsFalse(reopened.TryAttribute(RepoA, "peer-two"));
    }

    [TestMethod]
    public void Open_TheFileIsCorrupt_SetsItAsideAndStartsRefillable()
    {
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(_stateDirectory).FullName, "replica-owners.json"),
            "{ not json");

        // Recoverable state: the owner is whoever next offers the repository
        // over an authenticated session, so corruption is set aside, never
        // fatal — unlike the grants file, whose loss unpaired a fleet.
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one"));
        Assert.IsTrue(File.Exists(Path.Combine(_stateDirectory, "replica-owners.json.corrupt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
