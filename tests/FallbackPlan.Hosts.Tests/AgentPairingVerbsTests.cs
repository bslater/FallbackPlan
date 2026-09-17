using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's pairing management verbs (ADR-0030 §3): listing the grants a
/// device holds and revoking one — the revocation being unilateral at the
/// service, the party at risk — and re-pointing a replica stored here at a
/// different paired device with no service listening (ADR-0053 §3,
/// FR-REP-001), the file-direct arm of the verb.
/// </summary>
[TestClass]
public sealed class AgentPairingVerbsTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "fbp-agent-verbs", Guid.NewGuid().ToString("n"));

    private PeerIdentity Pin(string label)
    {
        var grants = PeerGrantStore.Open(_state);
        using var peer = PeerKeypair.Generate();
        grants.Pin(new PeerGrant(peer.Identity, label, PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
        return peer.Identity;
    }

    [TestMethod]
    public async Task Pairings_WithGrantsHeld_ListsEachByFingerprintAndLabel()
    {
        var one = Pin("laptop");
        var two = Pin("phone");

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "pairings", "--state", _state);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains(one.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.Contains(two.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.Contains("laptop", result.Output, StringComparison.Ordinal);
        Assert.Contains("phone", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Unpair_ByFingerprint_RemovesThatGrantAlone()
    {
        var keep = Pin("laptop");
        var drop = Pin("phone");

        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "unpair", "--state", _state, "--fingerprint", drop.Fingerprint);

        Assert.AreEqual(0, result.ExitCode, result.Error);

        var grants = PeerGrantStore.Open(_state);
        Assert.IsNull(grants.Find(drop));
        Assert.IsNotNull(grants.Find(keep));
    }

    [TestMethod]
    public async Task Unpair_AFingerprintThatMatchesNothing_IsRefusedAndChangesNothing()
    {
        Pin("laptop");

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "unpair", "--state", _state, "--fingerprint", "zzzzzzzz");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no pairing matches", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(1, PeerGrantStore.Open(_state).Grants.Count);
    }

    private const string RepositoryIdHex = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public async Task Reattribute_NoServiceListening_RePointsTheLedgerAndSaysSo()
    {
        var gone = Pin("gone");
        var rebuilt = Pin("rebuilt");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(RepositoryIdHex, gone.Fingerprint));

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", rebuilt.Fingerprint[..8]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("rebuilt", result.Output, StringComparison.Ordinal);
        Assert.Contains(gone.Fingerprint, result.Output, StringComparison.Ordinal);
        Assert.AreEqual(rebuilt.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);

        // On the record for whoever reads this machine next.
        Assert.IsTrue(NoticeStore.Open(_state).Unacknowledged
            .Any(notice => notice.Key == $"replica-reattributed:{RepositoryIdHex}"));
    }

    [TestMethod]
    public async Task Reattribute_WithoutItsArguments_IsUsage()
    {
        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "reattribute", "--state", _state, "--to", "ABCDEF");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--repository", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reattribute_AReplicaItsOwnerCanClaim_IsRefusedByNameEvenOffline()
    {
        // The file-direct arm refuses exactly what the service refuses: the
        // override is for the replica the passphrase cannot reach, and a
        // recorded claim key means the passphrase can.
        var gone = Pin("gone");
        var rebuilt = Pin("rebuilt");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(
            RepositoryIdHex, gone.Fingerprint, claimPublicKey: new string('e', 64)));

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", rebuilt.Fingerprint);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("claim", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(gone.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);
    }

    [TestMethod]
    public async Task Reattribute_AnAmbiguousPrefix_IsRefusedAndChangesNothing()
    {
        var gone = Pin("gone");
        Assert.IsTrue(ReplicaOwnerStore.Open(_state).TryAttribute(RepositoryIdHex, gone.Fingerprint));

        // A second pairing whose fingerprint begins as the first does —
        // minted until one does, which a 32-symbol alphabet makes cheap —
        // so that one symbol names two devices.
        var grants = PeerGrantStore.Open(_state);
        for (var attempt = 0; ; attempt++)
        {
            Assert.IsTrue(attempt < 500, "no keypair shared the first fingerprint symbol in 500 tries");
            using var twin = PeerKeypair.Generate();
            if (twin.Identity.Fingerprint[0] == gone.Fingerprint[0])
            {
                grants.Pin(new PeerGrant(twin.Identity, "twin", PeerRole.StoresHere, PeerTerms.None, 1_722_600_000_000));
                break;
            }
        }

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "reattribute", "--state", _state,
            "--repository", RepositoryIdHex, "--to", gone.Fingerprint[..1]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("more of the fingerprint", result.Error, StringComparison.Ordinal);
        Assert.AreEqual(gone.Fingerprint, ReplicaOwnerStore.Open(_state).Find(RepositoryIdHex)!.Fingerprint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
