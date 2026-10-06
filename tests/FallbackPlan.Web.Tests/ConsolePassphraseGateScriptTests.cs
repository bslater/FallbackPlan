namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's half of the passphrase gate (FR-WOR-007, ADR-0089), pinned
/// structurally: every action that names a backup's files unlocks a source
/// with the passphrase first, the restore wizard has no way past a passphrase
/// it could not check, and a change report says when the service withheld the
/// names only the backup holds. <c>PassphraseGateDomTests</c> walks the same
/// gate in a browser.
/// </summary>
[TestClass]
public sealed class ConsolePassphraseGateScriptTests
{
    private static string AppJs()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent!;
        }

        Assert.IsNotNull(directory, "the repository root (FallbackPlan.slnx) was not found above the test assembly");
        return File.ReadAllText(Path.Combine(
            directory.FullName, "src", "FallbackPlan.Web", "wwwroot", "app.js"));
    }

    private static string FunctionBody(string script, string name)
    {
        var start = script.IndexOf($"function {name}", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"app.js no longer declares '{name}'");

        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var alt = script.IndexOf("\nconst ", start + 1, StringComparison.Ordinal);
        if (alt >= 0 && (end < 0 || alt < end))
        {
            end = alt;
        }

        return end < 0 ? script[start..] : script[start..end];
    }

    /// <summary>One action handler's body, sliced from its key to the next action key.</summary>
    private static string ActionBody(string script, string name)
    {
        var start = script.IndexOf($"\"{name}\"(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"app.js no longer declares the '{name}' action");

        var end = script.IndexOf("\n  \"", start + 1, StringComparison.Ordinal);
        var alt = script.IndexOf("\n  async \"", start + 1, StringComparison.Ordinal);
        if (alt >= 0 && (end < 0 || alt < end))
        {
            end = alt;
        }

        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void EveryActionThatNamesABackupsFiles_UnlocksASourceFirst()
    {
        var script = AppJs();
        foreach (var action in new[] { "browse", "job-changes", "job-failures", "what-changed" })
        {
            Assert.Contains("unlockSource(", ActionBody(script, action), StringComparison.Ordinal, action);
        }
    }

    [TestMethod]
    public void TheRestoreWizard_HasNoWayPastAPassphraseItCouldNotCheck()
    {
        // The local-verification bypass went with the local verification: the
        // gate checks against what the service publishes, which a console on
        // any machine can do, so "cannot check" means "cannot proceed".
        var script = AppJs();
        Assert.DoesNotContain("without local verification", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rst-gate-ack", script, StringComparison.Ordinal);
        Assert.DoesNotContain("gateAck", script, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheChangeReport_SaysWhenTheServiceWithheldNames()
    {
        Assert.Contains("namesWithheld", FunctionBody(AppJs(), "comparisonReport"), StringComparison.Ordinal);
    }
}
