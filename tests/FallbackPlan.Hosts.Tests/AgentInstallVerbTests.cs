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
            "install", "--archives", "/srv/archives", "--state", "/var/lib/fallbackplan", "--target", "systemd");

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
            "install", "--archives", "/srv/archives", "--state", "/var/lib/fallbackplan");

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
            "install", "--archives", "/srv/archives", "--state", "/s", "--target", "plan9");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("unknown --target", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Install_WithoutTheArchivesFlag_BakesTheDefaultIntoTheDefinition()
    {
        // Path flags stopped being a prerequisite when the paths gained
        // defaults (FR-SVC-016): a definition printed without --archives
        // names the default root explicitly, so the registered service runs
        // on exactly the installation a bare `run` would.
        var result = await HostHarness.RunAsync(
            AgentHost.RunAsync, "install", "--state", "/var/lib/fallbackplan");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains(FallbackPlan.Api.InstallationDefaults.ArchivesRoot, result.Output, StringComparison.Ordinal);
        Assert.Contains("/var/lib/fallbackplan", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The state directory this verb names is the one the service will use once
    /// it exists — <c>/var/lib/fallbackplan</c> and its like — and the person
    /// running <c>install</c> is by definition the person who has not created it
    /// yet. Composing logging must not turn that into a failure.
    /// </summary>
    /// <remarks>
    /// The unwritable path is contrived rather than borrowed from the platform:
    /// its parent is an ordinary file, so no user can descend into it, root
    /// included. The defect this pins passed every local run precisely because
    /// those run as a user who can create anything, and failed CI on all three
    /// platforms at once.
    /// </remarks>
    [TestMethod]
    public async Task Install_WhenTheStateDirectoryCannotBeCreated_StillPrintsTheUnit()
    {
        var occupied = Path.Combine(Path.GetTempPath(), "fbp-install-" + Guid.NewGuid().ToString("n"));
        await File.WriteAllTextAsync(occupied, "a file has no children");

        try
        {
            var result = await HostHarness.RunAsync(
                AgentHost.RunAsync,
                "install", "--archives", "/srv/archives",
                "--state", Path.Combine(occupied, "state"), "--target", "systemd");

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.Contains("[Service]", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    /// <summary>
    /// "Never touches the system" includes not leaving a log directory behind.
    /// </summary>
    /// <remarks>
    /// Whoever runs this has enough privilege to register a service, and the
    /// state directory belongs to an account that does not exist yet — so a
    /// <c>logs</c> directory created here is one the service is about to be
    /// unable to write. Nothing in the type system stops the verb acquiring a
    /// file sink along with every other verb, so this asserts the absence.
    /// </remarks>
    [TestMethod]
    public async Task Install_WithAWritableStateDirectory_CreatesNothingInIt()
    {
        var state = Path.Combine(Path.GetTempPath(), "fbp-install-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(state);

        try
        {
            var result = await HostHarness.RunAsync(
                AgentHost.RunAsync,
                "install", "--archives", "/srv/archives", "--state", state, "--target", "systemd");

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.IsEmpty(Directory.GetFileSystemEntries(state));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}
