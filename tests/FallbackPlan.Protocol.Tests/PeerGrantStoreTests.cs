using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// Grants and terms (specification peer-protocol 01 §3–§4): what a pinned
/// pairing is, what survives a restart, and whose terms win.
/// </summary>
[TestClass]
public sealed class PeerGrantStoreTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-peer-tests", Guid.NewGuid().ToString("n"));

    private PeerGrantStore Open() => PeerGrantStore.Open(_stateDirectory);

    private static PeerGrant Grant(
        PeerIdentity identity,
        string label = "a friend's laptop",
        PeerRole role = PeerRole.StoresForUs,
        PeerTerms? terms = null) =>
        new(identity, label, role, terms ?? new PeerTerms(500_000_000_000, "every 1h", 4), 1_722_600_000_000);

    [TestMethod]
    public void A_pinned_grant_survives_a_restart()
    {
        using var keypair = PeerKeypair.Generate();

        Open().Pin(Grant(keypair.Identity));

        // A fresh store reads the file, which is the only thing that matters:
        // pairings are not rebuildable from the repository, so if they do not
        // survive a process restart they do not survive at all.
        var reopened = Open().Find(keypair.Identity);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(keypair.Identity, reopened!.Identity);
        Assert.AreEqual("a friend's laptop", reopened.Label);
        Assert.AreEqual(PeerRole.StoresForUs, reopened.Role);
        Assert.AreEqual(500_000_000_000ul, reopened.Terms.QuotaBytes);
        Assert.AreEqual(4u, reopened.Terms.RetentionFloorGenerations);
    }

    [TestMethod]
    public void A_peer_whose_key_differs_is_simply_not_found()
    {
        using var paired = PeerKeypair.Generate();
        using var impostor = PeerKeypair.Generate();

        var store = Open();
        store.Pin(Grant(paired.Identity, label: "the laptop"));

        // 01 §2.5's refusal falls out of the absence rather than out of a
        // comparison someone had to remember to write. An impostor presenting
        // the same label is still a different peer, because the label is not
        // what anything is keyed by.
        Assert.IsNotNull(store.Find(paired.Identity));
        Assert.IsNull(store.Find(impostor.Identity));
    }

    [TestMethod]
    public void Revoking_removes_the_pairing_and_says_whether_there_was_one()
    {
        using var keypair = PeerKeypair.Generate();

        var store = Open();
        store.Pin(Grant(keypair.Identity));

        Assert.IsTrue(store.Revoke(keypair.Identity));
        Assert.IsNull(store.Find(keypair.Identity));
        Assert.IsNull(Open().Find(keypair.Identity));

        // Revoking twice is not an error, but it is not a lie either.
        Assert.IsFalse(store.Revoke(keypair.Identity));
    }

    [TestMethod]
    public void A_label_can_be_changed_and_changes_nothing_else()
    {
        using var keypair = PeerKeypair.Generate();

        var store = Open();
        store.Pin(Grant(keypair.Identity, label: "laptop"));

        Assert.IsTrue(store.Relabel(keypair.Identity, "Ana's laptop"));

        var grant = store.Find(keypair.Identity);
        Assert.AreEqual("Ana's laptop", grant!.Label);

        // The label carries no authority, so editing it must not disturb the
        // thing that does.
        Assert.AreEqual(keypair.Identity, grant.Identity);
        Assert.AreEqual(PeerRole.StoresForUs, grant.Role);
    }

    [TestMethod]
    public void A_grant_file_that_cannot_be_read_is_refused_not_emptied()
    {
        using var keypair = PeerKeypair.Generate();
        Open().Pin(Grant(keypair.Identity));

        File.WriteAllText(Path.Combine(_stateDirectory, "peers.json"), "{ this is not grants");

        // The job journal is sacrificial by design; this is not. Starting empty
        // would silently unpair every peer this device has, which is a fleet-wide
        // outage presented as a clean start.
        var refused = Assert.ThrowsExactly<ClientStateException>(Open);
        Assert.Contains("not rebuildable", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void The_store_holds_no_more_grants_than_the_limit()
    {
        var store = Open();
        var keypairs = new List<PeerKeypair>();

        try
        {
            for (var i = 0; i < PeerGrantStore.MaximumGrants; i++)
            {
                var keypair = PeerKeypair.Generate();
                keypairs.Add(keypair);
                store.Pin(Grant(keypair.Identity));
            }

            using var overflow = PeerKeypair.Generate();
            Assert.ThrowsExactly<ClientStateException>(() => store.Pin(Grant(overflow.Identity)));

            // Re-pinning one already held is a replacement, not a new grant, so
            // a full store can still accept a re-pairing.
            store.Pin(Grant(keypairs[0].Identity, label: "renamed"));
            Assert.AreEqual("renamed", store.Find(keypairs[0].Identity)!.Label);
        }
        finally
        {
            foreach (var keypair in keypairs)
            {
                keypair.Dispose();
            }
        }
    }

    [TestMethod]
    public void Applying_narrower_terms_says_so()
    {
        using var keypair = PeerKeypair.Generate();

        var store = Open();
        store.Pin(Grant(keypair.Identity, terms: new PeerTerms(1_000, string.Empty, 4)));

        // A destination may change its terms whenever it likes and the source
        // continues under them. Narrowing is the case the source must surface,
        // because the alternative is replication that quietly stops.
        Assert.IsTrue(store.ApplyTerms(keypair.Identity, new PeerTerms(500, string.Empty, 4)));
        Assert.AreEqual(500ul, store.Find(keypair.Identity)!.Terms.QuotaBytes);

        Assert.IsFalse(store.ApplyTerms(keypair.Identity, new PeerTerms(2_000, string.Empty, 4)));
    }

    [TestMethod]
    public void Applying_terms_to_a_peer_that_is_not_paired_is_refused()
    {
        using var stranger = PeerKeypair.Generate();

        Assert.ThrowsExactly<ClientStateException>(
            () => Open().ApplyTerms(stranger.Identity, PeerTerms.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}

/// <summary>
/// The destination's terms (specification peer-protocol 01 §4): its disk, its
/// rules.
/// </summary>
[TestClass]
public sealed class PeerTermsTests
{
    [TestMethod]
    public void A_source_may_ask_for_less_but_never_for_more()
    {
        var offered = new PeerTerms(1_000, "every 1h", 4);

        Assert.IsTrue(new PeerTerms(1_000, "every 1h", 4).IsWithin(offered));
        Assert.IsTrue(new PeerTerms(500, "every 1h", 2).IsWithin(offered));

        // More quota, or a deeper retention floor, is asking the destination to
        // do something it did not agree to.
        Assert.IsFalse(new PeerTerms(1_001, "every 1h", 4).IsWithin(offered));
        Assert.IsFalse(new PeerTerms(1_000, "every 1h", 5).IsWithin(offered));
    }

    [TestMethod]
    public void Nothing_is_within_terms_that_permit_nothing()
    {
        Assert.IsTrue(PeerTerms.None.IsWithin(PeerTerms.None));
        Assert.IsFalse(new PeerTerms(1, string.Empty, 0).IsWithin(PeerTerms.None));
    }

    [TestMethod]
    public void Narrowing_is_recognised_on_every_axis_a_source_relies_on()
    {
        var before = new PeerTerms(1_000, string.Empty, 4);

        Assert.IsTrue(new PeerTerms(999, string.Empty, 4).Narrows(before));
        Assert.IsTrue(new PeerTerms(1_000, string.Empty, 3).Narrows(before));

        // Gaining a window where there was none is a narrowing too: transfers
        // that could run at any time now cannot.
        Assert.IsTrue(new PeerTerms(1_000, "every 1h", 4).Narrows(before));

        Assert.IsFalse(new PeerTerms(1_000, string.Empty, 4).Narrows(before));
        Assert.IsFalse(new PeerTerms(2_000, string.Empty, 8).Narrows(before));
    }
}
