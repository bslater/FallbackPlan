using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service host's <c>install</c> verb (ADR-0033): it prints the definition
/// that would register the agent as a service — to standard output, so it can be
/// redirected to a file — and never touches the system. The apply guidance and
/// the pre-seed reminder go to standard error.
/// </summary>
[TestClass]
public sealed class AgentInstallVerbTests
{
    [TestMethod]
    public async Task Install_ForSystemd_PrintsTheUnitAndTheUnlockReminder()
    {
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "install", "--repo", "/srv/repo", "--state", "/var/lib/fallbackplan", "--target", "systemd");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains("[Service]", result.Output, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", result.Output, StringComparison.Ordinal);
        // The reminder to seed the keystore as the service account is guidance,
        // so it is on standard error, not in the redirectable artifact.
        Assert.Contains("unlock", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unlock", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Install_WithNoTarget_DefaultsToThisPlatform()
    {
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "install", "--repo", "/srv/repo", "--state", "/var/lib/fallbackplan");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("WantedBy=multi-user.target", result.Output, StringComparison.Ordinal);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("<plist", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("sc.exe create", result.Output, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Install_ForAnUnknownTarget_IsRefused()
    {
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "install", "--repo", "/srv/repo", "--state", "/s", "--target", "plan9");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("unknown --target", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Install_WithoutRepo_IsRefused()
    {
        var result = await HostHarness.RunAsync(AgentHost.RunAsync, "install", "--state", "/var/lib/fallbackplan");

        Assert.AreEqual(1, result.ExitCode);
    }
}
