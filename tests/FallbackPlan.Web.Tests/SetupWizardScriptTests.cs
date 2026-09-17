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

    /// <summary>
    /// One action handler's body from the <c>setupActions</c> table, sliced
    /// from its key to the next action key.
    /// </summary>
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
    public void TheCombinedPassphraseStep_GatesBuildOnStrengthAndMatch()
    {
        // The user's spec for the wizard's passphrase step: one screen with
        // the passphrase AND its confirmation, and "Set the passphrase"
        // disabled until the strength policy passes and the two entries
        // match.
        var script = AppJs();
        var step = FunctionBody(script, "setupStep2");

        Assert.Contains("id=\"setup-pass\"", step, StringComparison.Ordinal);
        Assert.Contains("id=\"setup-confirm\"", step, StringComparison.Ordinal,
            "the confirmation belongs on the same step as the passphrase");
        Assert.Contains("id=\"setup-match\"", step, StringComparison.Ordinal,
            "the mismatch hint needs a stable container so typing patches it in place");
        Assert.Contains("data-action=\"setup-finish\"", step, StringComparison.Ordinal);
        Assert.Contains("Set the passphrase", step, StringComparison.Ordinal);

        var gate = FunctionBody(script, "setupBuildReady");
        Assert.Contains("strength?.acceptable", gate, StringComparison.Ordinal,
            "the build button must gate on the server's strength verdict");
        Assert.Contains("confirmation === U.passphrase", gate, StringComparison.Ordinal,
            "the build button must gate on the confirmation matching");

        Assert.Contains("\"setup-finish\"", FunctionBody(script, "setupApplyStrength"), StringComparison.Ordinal,
            "the in-place patch must keep the build button's disabled state current while typing");
    }

    [TestMethod]
    public void TheWizard_HasNoKitStep_AndNoKitAction()
    {
        // The recovery kit is withdrawn (ADR-0060): the passphrase is the
        // whole credential, so the ceremony goes straight from the
        // passphrase to the first account. A kit step creeping back would be
        // a second artefact for a person to lose.
        var script = AppJs();

        Assert.Contains("const SETUP_STEPS = [\"What this is\", \"Passphrase\", \"Account\"];", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("setup-kit-", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/recovery-kit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("confirm_recovery_kit", script, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheAccountStep_GatesCreateUserOnEveryRule()
    {
        // The last step of the user's spec: username, password, confirmation;
        // Create User enabled only when the server accepts the password, it
        // is not the passphrase (compared by hash), and the confirmation
        // matches.
        var script = AppJs();
        var step = FunctionBody(script, "setupStep3");

        Assert.Contains("id=\"setup-user\"", step, StringComparison.Ordinal);
        Assert.Contains("id=\"setup-user-pass\"", step, StringComparison.Ordinal);
        Assert.Contains("id=\"setup-user-confirm\"", step, StringComparison.Ordinal);
        Assert.Contains("id=\"setup-account-rules\"", step, StringComparison.Ordinal,
            "the rule checklist needs a stable container so typing patches it in place");
        Assert.Contains("data-action=\"setup-create-user\"", step, StringComparison.Ordinal);

        var gate = FunctionBody(script, "setupAccountReady");
        Assert.Contains("check?.acceptable", gate, StringComparison.Ordinal,
            "Create User must gate on the server's password verdict");
        Assert.Contains("hash !== U.passHash", gate, StringComparison.Ordinal,
            "Create User must refuse a password equal to the passphrase — compared by hash");
        Assert.Contains("confirm === a.password", gate, StringComparison.Ordinal,
            "Create User must gate on the confirmation matching");

        var check = FunctionBody(script, "setupSchedulePasswordCheck");
        Assert.Contains("setupApplyAccount(", check, StringComparison.Ordinal);
        Assert.DoesNotContain("setupRender(", check, StringComparison.Ordinal,
            "the checklist answer must patch in place — a re-render replaces the field being typed in");
    }

    [TestMethod]
    public void ThePassphraseHash_IsCapturedBeforeTheSecretIsCleared()
    {
        // The account step compares by hash precisely so the passphrase is
        // not held past provisioning. That only works if the hash is taken
        // BEFORE the secret is wiped.
        var script = AppJs();

        foreach (var action in new[] { "setup-finish" })
        {
            var body = ActionBody(script, action);
            var hashed = body.IndexOf("passHash = await sha256Hex(", StringComparison.Ordinal);
            var cleared = body.IndexOf("U.passphrase = \"\"", StringComparison.Ordinal);

            Assert.IsTrue(hashed >= 0, $"'{action}' must capture the passphrase hash");
            Assert.IsTrue(cleared >= 0, $"'{action}' must clear the passphrase");
            Assert.IsTrue(hashed < cleared, $"'{action}' must hash before it clears — afterwards there is nothing to hash");
        }
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
