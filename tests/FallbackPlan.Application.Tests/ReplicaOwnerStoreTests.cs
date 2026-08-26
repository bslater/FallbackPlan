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

    // ------------------------------------------------ the claim credential

    private static byte[] Token(byte fill) => [.. Enumerable.Repeat(fill, 16)];

    private const string PublicKey = "9181d3460816a92b16e53cfc8bebdfb815426eee35e0a5603201b76c1bc770ad";

    [TestMethod]
    public void Open_TheOlderFlatFile_IsMigratedRatherThanSetAside()
    {
        // Before the claim ceremony each value was the owning fingerprint as a
        // bare string. Reading that as the newer shape throws, and a catch that
        // treated the throw as corruption would silently unattribute every
        // replica the destination holds — the quota gone, every retention
        // command unvalidatable. A format change is not damage.
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(
            Path.Combine(_stateDirectory, "replica-owners.json"),
            $$"""{ "{{RepoA}}": "peer-one", "{{RepoB}}": "peer-two" }""");

        var store = ReplicaOwnerStore.Open(_stateDirectory);

        Assert.ContainsSingle(store.OwnedBy("peer-one"));
        Assert.ContainsSingle(store.OwnedBy("peer-two"));
        Assert.IsFalse(File.Exists(Path.Combine(_stateDirectory, "replica-owners.json.corrupt")));
        Assert.IsFalse(store.TryAttribute(RepoA, "peer-two"), "the migrated attribution still stands");
    }

    [TestMethod]
    public void OfferClaimToken_BeforeAnythingIsRegistered_MintsOnceAndRepeatsIt()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");

        var first = store.OfferClaimToken(RepoA, () => Token(0xD0));
        var second = store.OfferClaimToken(RepoA, () => Token(0xEE));

        Assert.IsNotNull(first);
        Assert.AreEqual(first, second, "a second offer must not re-mint and orphan the first token");
    }

    [TestMethod]
    public void OfferClaimToken_OnceACredentialIsRegistered_OffersNothingFurther()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");
        var token = store.OfferClaimToken(RepoA, () => Token(0xD0))!;

        Assert.IsTrue(store.TryRegisterClaimKey(RepoA, PublicKey));

        // The destination asks exactly once. Re-offering would invite a source
        // to derive a second keypair and register nothing, leaving the replica
        // claimable only by a credential nobody holds.
        Assert.IsNull(store.OfferClaimToken(RepoA, () => Token(0xEE)));
        Assert.AreEqual(token, store.Find(RepoA)!.ClaimTokenHex);
    }

    [TestMethod]
    public void TryRegisterClaimKey_WithNoTokenOffered_IsRefused()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");

        Assert.IsFalse(store.TryRegisterClaimKey(RepoA, PublicKey));
        Assert.IsNull(store.Find(RepoA)!.ClaimPublicKeyHex);
    }

    [TestMethod]
    public void TryRegisterClaimKey_ASecondTime_IsRefusedRatherThanReplacing()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");
        store.OfferClaimToken(RepoA, () => Token(0xD0));
        store.TryRegisterClaimKey(RepoA, PublicKey);

        // Replacing it would let whoever spoke last decide who may recover.
        Assert.IsFalse(store.TryRegisterClaimKey(RepoA, new string('b', 64)));
        Assert.AreEqual(PublicKey, store.Find(RepoA)!.ClaimPublicKeyHex);
    }

    [TestMethod]
    public void ClaimableBy_NamesOnlyRegisteredReplicasThisIdentityDoesNotOwn()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");
        store.TryAttribute(RepoB, "peer-two");
        store.OfferClaimToken(RepoA, () => Token(0xD0));
        store.TryRegisterClaimKey(RepoA, PublicKey);

        // RepoB carries no credential, so it is not offered as a candidate at
        // all — an unregistered replica is not claimable and must not appear.
        CollectionAssert.AreEqual(new[] { RepoA }, store.ClaimableBy("rebuilt-peer").ToArray());
        Assert.IsEmpty(store.ClaimableBy("peer-one"), "you are not challenged to claim what you already own");
    }

    [TestMethod]
    public void TryReattribute_AfterAProofVerified_MovesTheAttributionAndWaitsForTheOperator()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "lost-machine");
        store.OfferClaimToken(RepoA, () => Token(0xD0));
        store.TryRegisterClaimKey(RepoA, PublicKey);

        Assert.IsTrue(store.TryReattribute(RepoA, "rebuilt-machine"));

        Assert.ContainsSingle(store.OwnedBy("rebuilt-machine"));
        Assert.IsEmpty(store.OwnedBy("lost-machine"));

        // Reading is available at once; deleting waits for the person who owns
        // the disk (peer-protocol 06 §3).
        Assert.IsTrue(store.IsClaimAwaitingAcknowledgement(RepoA));
    }

    [TestMethod]
    public void TryReattribute_WithNoRegisteredCredential_IsRefused()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "lost-machine");

        Assert.IsFalse(store.TryReattribute(RepoA, "rebuilt-machine"));
        Assert.ContainsSingle(store.OwnedBy("lost-machine"));
    }

    [TestMethod]
    public void TryReattribute_ByTheIdentityThatAlreadyOwnsIt_MovesNothingAndRaisesNoNotice()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "peer-one");
        store.OfferClaimToken(RepoA, () => Token(0xD0));
        store.TryRegisterClaimKey(RepoA, PublicKey);

        Assert.IsTrue(store.TryReattribute(RepoA, "peer-one"));

        Assert.ContainsSingle(store.OwnedBy("peer-one"));
        Assert.IsFalse(
            store.IsClaimAwaitingAcknowledgement(RepoA),
            "nothing moved, so there is nothing for an operator to acknowledge");
    }

    [TestMethod]
    public void AcknowledgeClaim_ReleasesTheGate_AndIsIdempotent()
    {
        var store = ReplicaOwnerStore.Open(_stateDirectory);
        store.TryAttribute(RepoA, "lost-machine");
        store.OfferClaimToken(RepoA, () => Token(0xD0));
        store.TryRegisterClaimKey(RepoA, PublicKey);
        store.TryReattribute(RepoA, "rebuilt-machine");

        store.AcknowledgeClaim(RepoA);
        store.AcknowledgeClaim(RepoA);

        Assert.IsFalse(store.IsClaimAwaitingAcknowledgement(RepoA));
    }

    [TestMethod]
    public void Open_AfterAClaim_ReadsTheCredentialAndTheGateBack()
    {
        var first = ReplicaOwnerStore.Open(_stateDirectory);
        first.TryAttribute(RepoA, "lost-machine");
        var token = first.OfferClaimToken(RepoA, () => Token(0xD0))!;
        first.TryRegisterClaimKey(RepoA, PublicKey);
        first.TryReattribute(RepoA, "rebuilt-machine");

        // A restart must not release the retention gate, and must not forget
        // the credential a second recovery would need.
        var reopened = ReplicaOwnerStore.Open(_stateDirectory);
        var attribution = reopened.Find(RepoA)!;

        Assert.AreEqual("rebuilt-machine", attribution.Fingerprint);
        Assert.AreEqual(token, attribution.ClaimTokenHex);
        Assert.AreEqual(PublicKey, attribution.ClaimPublicKeyHex);
        Assert.IsTrue(attribution.ClaimAwaitingAcknowledgement);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
