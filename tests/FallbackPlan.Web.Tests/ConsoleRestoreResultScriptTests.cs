namespace FallbackPlan.Web.Tests;

/// <summary>
/// The restore wizard's result step, pinned structurally (contract 1.45,
/// FR-RST-007): a restore that read some files around damage says how many
/// and shows the service's own lines naming each file's copy — the one it
/// came from and the one it was read around — so the person who met the
/// damage hears of it where they are looking, not only in a notice.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleReceiptsScriptTests"/>: no browser, the script's own
/// text, failing with the reason spelled out. The DOM suite walks the step
/// with a restore that read around damage where a browser is provisioned.
/// </remarks>
[TestClass]
public sealed class ConsoleRestoreResultScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

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

    [TestMethod]
    public void TheResultStep_SaysWhichFilesWereReadAroundDamage()
    {
        var step = FunctionBody(AppJs(), "rstStep6");

        Assert.Contains("W.result.readAround", step, StringComparison.Ordinal, "the count is not shown");
        Assert.Contains(
            "W.result.readAroundSample", step, StringComparison.Ordinal,
            "the lines naming each file's copies are not shown");

        // Escaped like every other line the service writes: a path is text a
        // backup recorded, never markup.
        Assert.Contains("esc(W.result.readAroundSample", step, StringComparison.Ordinal);
    }
}
