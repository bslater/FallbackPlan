using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service-manager definitions the <c>install</c> verb generates (ADR-0033).
/// Pure text with no platform calls, so each target is checked on any host — the
/// point being that what an operator would apply names the right executable, the
/// right <c>--repo</c>/<c>--state</c>, a restart policy, and the account the
/// keystore unlock is scoped to.
/// </summary>
[TestClass]
public sealed class ServiceUnitTests
{
    private static ServiceUnitOptions Options(
        string? account = "fbp", string? remoteInterface = null, string? remotePort = null) =>
        new(
            "/opt/fallbackplan/fallbackplan-agent",
            "/srv/repo",
            "/var/lib/fallbackplan",
            account,
            "FallbackPlan",
            "com.fallbackplan.agent",
            remoteInterface,
            remotePort);

    [TestMethod]
    public void Systemd_NamesTheExecCommandRestartPolicyAndBootTarget()
    {
        var unit = ServiceUnit.Systemd(Options());

        Assert.Contains("Type=simple", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=", unit, StringComparison.Ordinal);
        Assert.Contains("/opt/fallbackplan/fallbackplan-agent", unit, StringComparison.Ordinal);
        Assert.Contains("--repo", unit, StringComparison.Ordinal);
        Assert.Contains("/srv/repo", unit, StringComparison.Ordinal);
        Assert.Contains("--state", unit, StringComparison.Ordinal);
        Assert.Contains("/var/lib/fallbackplan", unit, StringComparison.Ordinal);
        Assert.Contains("User=fbp", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Systemd_WithoutAnAccount_OmitsTheUserLine()
    {
        var unit = ServiceUnit.Systemd(Options(account: null));

        Assert.DoesNotContain("User=", unit, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Systemd_WithARemoteBinding_CarriesTheRemoteFlags()
    {
        var unit = ServiceUnit.Systemd(Options(remoteInterface: "0.0.0.0", remotePort: "9000"));

        Assert.Contains("--remote-interface", unit, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0", unit, StringComparison.Ordinal);
        Assert.Contains("--remote-port", unit, StringComparison.Ordinal);
        Assert.Contains("9000", unit, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Launchd_HasTheLabelProgramArgumentsAndBootKeys()
    {
        var plist = ServiceUnit.Launchd(Options());

        Assert.Contains("<key>Label</key>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>com.fallbackplan.agent</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>ProgramArguments</key>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--repo</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/srv/repo</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>UserName</key>", plist, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Windows_CreatesAnAutoStartServiceWithAQuotedBinPath()
    {
        var commands = ServiceUnit.Windows(Options(account: null));

        Assert.Contains("sc.exe create FallbackPlan", commands, StringComparison.Ordinal);
        Assert.Contains("start= auto", commands, StringComparison.Ordinal);
        // The exe path and each argument are quoted inside the single binPath
        // value, so a space in the path survives sc.exe's own parsing.
        Assert.Contains("binPath= \"\\\"/opt/fallbackplan/fallbackplan-agent\\\"", commands, StringComparison.Ordinal);
        Assert.Contains("sc.exe start FallbackPlan", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("obj=", commands, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Windows_WithAnAccount_SetsObj()
    {
        var commands = ServiceUnit.Windows(Options(account: ".\\fbpsvc"));

        Assert.Contains("obj= \".\\fbpsvc\"", commands, StringComparison.Ordinal);
    }
}
