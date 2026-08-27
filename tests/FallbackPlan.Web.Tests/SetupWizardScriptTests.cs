namespace FallbackPlan.Web.Tests;

/// <summary>
/// The setup wizard's strength meter updates while the operator is typing
/// the passphrase, so its answer must patch the meter in place — never
/// rebuild the step.
/// </summary>
/// <remarks>
/// <para>
/// <c>setupRender()</c> replaces the whole step via <c>innerHTML</c>. Run
/// from the strength fetch's callback it destroys the very field being
/// typed in: focus is re-applied to a fresh element with the caret at a
/// browser-decided position, so the cursor jumps on every debounced answer
/// — roughly every quarter second plus a round trip — and keystrokes in
/// flight between the render and the refocus can die. The input handler
/// already states the rule ("no re-render here: it would replace the field
/// the person is typing in"); the strength path broke it.
/// </para>
/// <para>
/// Like <see cref="ConsoleShellLayerTests"/>, the rule is structural and so
/// is the test: no browser, just the script's own text, failing with the
/// reason spelled out if the strength callback ever reaches for the full
/// re-render again.
/// </para>
/// </remarks>
[TestClass]
public sealed class SetupWizardScriptTests
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

    /// <summary>
    /// The named function's body, from its declaration to the next top-level
    /// function declaration — coarse, and enough: the page's functions are
    /// declared at column zero.
    /// </summary>
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
    public void TheStrengthAnswer_PatchesTheMeter_NeverReRendersTheStep()
    {
        var body = FunctionBody(AppJs(), "setupScheduleStrength");

        Assert.DoesNotContain(
            "setupRender(", body,
            StringComparison.Ordinal,
            "the strength callback re-renders the whole step: the passphrase field is replaced "
            + "mid-typing, the caret jumps to wherever refocus lands it, and in-flight keystrokes "
            + "can die. Patch the meter in place (setupApplyStrength) instead.");

        Assert.Contains(
            "setupApplyStrength(", body,
            StringComparison.Ordinal,
            "the strength callback must apply its answer through the in-place patch, so the "
            + "field being typed in is never touched");
    }

    [TestMethod]
    public void TheStepTemplate_KeepsTheStableStrengthContainer()
    {
        // The patch target: an always-present container the answer writes
        // into. Without it the patch has nowhere to land and the only way to
        // show a strength change is the re-render the test above forbids.
        Assert.Contains("id=\"setup-strength\"", AppJs(), StringComparison.Ordinal);
    }
}
