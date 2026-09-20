namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's format-upgrade control, pinned structurally (contract 1.36;
/// [ADR-0066](../../docs/adr/0066-the-format-upgrade-record.md), FR-SVC-015,
/// NFR-COMP-004): the act hangs off the notice the service raises for a set
/// below the latest format and off nothing else, it is guarded by a typed
/// confirmation because it cannot be undone, and it sends
/// <c>upgrade_set_format</c> from an action rather than from a poller.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleReceiptsScriptTests"/>: no browser, the script's
/// own text, failing with the reason spelled out.
/// </remarks>
[TestClass]
public sealed class ConsoleFormatUpgradeScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    [TestMethod]
    public void TheButton_IsOfferedOnlyForTheNoticeThatAsksForIt()
    {
        var script = AppJs();

        Assert.Contains(
            "notice.key?.startsWith(\"format-upgradable:\")", script, StringComparison.Ordinal,
            "the control hangs off the service's own notice, so a set already at the latest never shows it");
        Assert.Contains(
            "data-action=\"upgrade-format\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "!notice.acknowledgedAt && notice.key?.startsWith(\"format-upgradable:\")",
            script,
            StringComparison.Ordinal,
            "an acknowledged notice is history; it must not still offer the act");
    }

    [TestMethod]
    public void TheAct_IsBehindATypedConfirmation_BecauseNothingUndoesIt()
    {
        var script = AppJs();

        Assert.Contains("data-word=\"upgrade\"", script, StringComparison.Ordinal);
        Assert.Contains("data-enables=\"upgrade-format-go\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "id=\"upgrade-format-go\" data-action=\"upgrade-format-go\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "cannot be undone", script, StringComparison.Ordinal,
            "the dialog says what upgrading costs before it is typed, not after");
    }

    [TestMethod]
    public void TheCommand_IsSentByTheAction_AndItsLinesAreReported()
    {
        var script = AppJs();
        var start = script.IndexOf("async \"upgrade-format-go\"(el)", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "app.js no longer declares the upgrade-format-go action");

        var body = script[start..script.IndexOf("\n  },\n", start, StringComparison.Ordinal)];

        Assert.Contains("command: \"upgrade_set_format\"", body, StringComparison.Ordinal);
        Assert.Contains("setName: el.dataset.set", body, StringComparison.Ordinal);
        Assert.Contains(
            "result?.result === \"configuration_change\"", body, StringComparison.Ordinal,
            "the answer is taken by its discriminator, as every other change is");
        Assert.Contains("reportDialog(", body, StringComparison.Ordinal);
        Assert.Contains(
            "refreshNotices()", body, StringComparison.Ordinal,
            "the notice the service resolved has to leave the card");
    }
}
