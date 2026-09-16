using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The replica attribution store (peer-protocol 05 §2): which peer each
/// replica repository belongs to — the quota's denominator, and later the
/// authority a retention command is validated against.
/// <para>
/// That "later" is now (FR-GC-008, ADR-0055 §5): the attribution carries the
/// repository's reclaim public key, recorded at first attribution and never
/// replaceable by a later offer, because the peer sending deletion
/// instructions is the peer that would like the key they are checked against
/// to be its own.
/// </para>
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

    [TestMethod]
    public void Attribute_TheReclaimPublicKey_IsRecordedWithTheFirstOffer()
    {
        // A destination holds no repository keys, so the key published at
        // attribution is the only thing it can check a deletion instruction
        // against (ADR-0055 §5).
        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64)));

        var owner = store.Find(RepoA);
        Assert.IsNotNull(owner);
        Assert.AreEqual("peer-one", owner.Fingerprint);
        Assert.AreEqual(new string('a', 64), owner.ReclaimPublicKey);
    }

    [TestMethod]
    public void Attribute_ALaterOffer_CannotReplaceTheRecordedKey()
    {
        // The attack this closes. Whoever sends deletion instructions is
        // whoever would like the key they are checked against to be theirs;
        // a key a later offer could overwrite is a check the attacker owns.
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64)));

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('b', 64)));

        Assert.AreEqual(new string('a', 64), store.Find(RepoA)!.ReclaimPublicKey);
    }

    [TestMethod]
    public void Attribute_AnAttributionThatRecordedNoKey_MayStillLearnOne()
    {
        // The upgrade path. A peering established before the decision, or by
        // a source that published nothing, would otherwise have to be torn
        // down and rebuilt to ever be secured — filling an absence is not
        // replacing an answer.
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one"));
        Assert.IsNull(store.Find(RepoA)!.ReclaimPublicKey);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('c', 64)));
        Assert.AreEqual(new string('c', 64), store.Find(RepoA)!.ReclaimPublicKey);
    }

    [TestMethod]
    public void Attribute_AnotherPeersRepository_IsStillRefusedAndLearnsNothing()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64)));

        Assert.IsFalse(store.TryAttribute(RepoA, "peer-two", new string('d', 64)));
        Assert.AreEqual("peer-one", store.Find(RepoA)!.Fingerprint);
        Assert.AreEqual(new string('a', 64), store.Find(RepoA)!.ReclaimPublicKey);
    }

    [TestMethod]
    public void Attribute_TheClaimPublicKey_IsRecordedAndNeverReplaced()
    {
        // Same rule as the reclaim key's (ADR-0055 §5), and it matters more
        // here: the claim key decides which device this peer will hand the
        // replica back to. A key a later offer could overwrite would let
        // whoever can reach the peer nominate themselves as the owner.
        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64), new string('e', 64)));
        Assert.AreEqual(new string('e', 64), store.Find(RepoA)!.ClaimPublicKey);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64), new string('f', 64)));
        Assert.AreEqual(new string('e', 64), store.Find(RepoA)!.ClaimPublicKey);
    }

    [TestMethod]
    public void Attribute_AnAttributionThatRecordedNoClaimKey_MayStillLearnOne()
    {
        // The self-healing half, and the whole of what makes an existing
        // peering claimable: one more offer from an updated source fills the
        // absence. What it cannot help is a machine that died before that
        // offer — ADR-0053 §3's operator path is the answer there, and is not
        // built.
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64)));
        Assert.IsNull(store.Find(RepoA)!.ClaimPublicKey);

        Assert.IsTrue(store.TryAttribute(RepoA, "peer-one", new string('a', 64), new string('g', 64)));
        Assert.AreEqual(new string('g', 64), store.Find(RepoA)!.ClaimPublicKey);
        Assert.AreEqual(
            new string('a', 64), store.Find(RepoA)!.ReclaimPublicKey, "filling one must not disturb the other");
    }

    [TestMethod]
    public void Open_AFileRecordedBeforeTheClaimKey_ReadsBackWithNone()
    {
        // The JSON tolerates a missing property, so an attribution written
        // before this field reads with it null and rewrites in the new shape
        // on the next offer — the same lift the pre-reclaim shape gets.
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(_stateDirectory).FullName, "replica-owners.json"),
            $$"""{ "{{RepoA}}": { "Fingerprint": "peer-one", "ReclaimPublicKey": "{{new string('a', 64)}}" } }""");

        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.IsFalse(File.Exists(Path.Combine(_stateDirectory, "replica-owners.json.corrupt")));
        Assert.AreEqual(new string('a', 64), store.Find(RepoA)!.ReclaimPublicKey);
        Assert.IsNull(store.Find(RepoA)!.ClaimPublicKey);
    }

    [TestMethod]
    public void Open_AFileInThePreReclaimShape_IsLiftedRatherThanSetAside()
    {
        // The old shape was a flat id-to-fingerprint map. Discarding it would
        // stop the quota being enforceable and make every peer re-offer to be
        // recognised — too high a price for a field that was simply not there
        // yet.
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(_stateDirectory).FullName, "replica-owners.json"),
            $$"""{ "{{RepoA}}": "peer-one" }""");

        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.IsFalse(
            File.Exists(Path.Combine(_stateDirectory, "replica-owners.json.corrupt")),
            "a readable older file is not corruption");
        Assert.AreEqual("peer-one", store.Find(RepoA)!.Fingerprint);
        Assert.IsNull(store.Find(RepoA)!.ReclaimPublicKey);
        Assert.IsNull(store.Find(RepoA)!.ClaimPublicKey);
        Assert.ContainsSingle(store.OwnedBy("peer-one"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
