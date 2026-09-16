using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// FR-USR-006's first carve-out: `fallbackplan-agent` <b>is</b> the service,
/// not a client of one, so scheduled work runs with nobody signed in.
/// </summary>
/// <remarks>
/// Asserted rather than assumed, because it is the assumption that would be
/// easiest to break by accident and the most damaging to break: a backup
/// product that needed somebody logged in before it would back up would not
/// be a backup product.
/// </remarks>
[TestClass]
public sealed class UnattendedWorkTests : IDisposable
{
    private readonly HostHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [TestMethod]
    public async Task ScheduledWork_RunsWithAnOwnerAccountAndNobodySignedIn()
    {
        // Setup leaves the owner account and the installation credential
        // behind (ADR-0044); the run holds no passphrase and needs nobody
        // present (ADR-0042 §5).
        await _harness.SetupAsync();
        _harness.WriteConfiguration("every 1h");

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--once", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory);

        Assert.AreEqual(0, result.ExitCode, result.All);
    }
}
