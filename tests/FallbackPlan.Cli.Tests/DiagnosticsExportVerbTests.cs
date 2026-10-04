namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The <c>diagnostics-export</c> verb's own refusals (ADR-0081), before any
/// service is asked: the CLI half of NFR-PRIV-003's bundle. What the bundle
/// carries is proved against a running service in
/// <c>Hosts.Tests/DiagnosticBundleTests</c>.
/// </summary>
/// <remarks>
/// Like <c>logs</c>, this is a verb with no local fallback: the log it carries
/// lives in a running service's memory, so the verb has to be told which
/// service to ask. And it writes a file a person is about to send somebody,
/// so the one thing it must never do is write over a file that was already
/// there.
/// </remarks>
[TestClass]
public sealed class DiagnosticsExportVerbTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task DiagnosticsExport_WithNeitherStateNorConnect_RefusesNamingBothWaysToSayWhichService()
    {
        var result = await CliHarness.RunRawAsync(
            "diagnostics-export", Path.Combine(_cli.WorkPath, "bundle.zip"));

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("--state", result.All, StringComparison.Ordinal);
        Assert.Contains("--connect", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DiagnosticsExport_OntoAFileThatExists_RefusesWithoutAskingAnyService()
    {
        Directory.CreateDirectory(_cli.WorkPath);
        var existing = Path.Combine(_cli.WorkPath, "keep-me.zip");
        await File.WriteAllTextAsync(existing, "somebody's file");

        // Nothing listens on this state directory, so a refusal that names the
        // file proves the check came first.
        var result = await CliHarness.RunRawAsync("diagnostics-export", existing, "--state", _cli.WorkPath);

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("keep-me.zip", result.All, StringComparison.Ordinal);
        Assert.Contains("exists", result.All, StringComparison.Ordinal);
        Assert.AreEqual("somebody's file", await File.ReadAllTextAsync(existing));
    }

    [TestMethod]
    public async Task DiagnosticsExport_Help_DescribesTheOptInAndThatItNeedsARunningService()
    {
        var result = await CliHarness.RunRawAsync("diagnostics-export", "--help");

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--include-paths", result.All, StringComparison.Ordinal);
        Assert.Contains("running service", result.All, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _cli.Dispose();
}
