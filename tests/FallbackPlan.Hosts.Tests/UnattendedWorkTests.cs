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
        _harness.WriteConfiguration("every 1h");
        UserStore.Open(_harness.StateDirectory).Create(
            "ben", "The-0wner-passw0rd",
            parameters: new Domain.Configuration.Argon2Parameters
            {
                MemoryKiB = 64,
                Iterations = 1,
                Parallelism = 1,
            });

        Environment.SetEnvironmentVariable(
            _harness.PassphraseVariable, "the one long passphrase of this installation");

        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--once", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(0, result.ExitCode, result.All);
    }
}
