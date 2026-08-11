using FallbackPlan.Agent;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service's pairing management verbs (ADR-0030 §3): listing the grants a
/// device holds and revoking one — the revocation being unilateral at the
/// service, the party at risk.
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

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }
}
