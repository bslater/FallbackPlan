namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's card of the restores the service kept receipts for, pinned
/// structurally (contract 1.73; FR-RST-004,
/// [ADR-0125](../../docs/adr/0125-the-service-lists-the-restore-receipts-it-keeps.md)):
/// it is filled from <c>list_restore_receipts</c> taken by its discriminator,
/// so a service before 1.73 reads as an empty card rather than a toast; it is
/// fetched on entering the Maintenance view and never by the pollers; and a
/// row the service could not read says so, rather than reading as a run that
/// restored nothing.
/// </summary>
[TestClass]
public sealed class ConsoleRestoreReceiptsScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    private static string FunctionBody(string script, string name)
    {
        var start = script.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"app.js no longer declares '{name}'");

        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var asynchronous = script.IndexOf("\nasync function ", start + 1, StringComparison.Ordinal);
        if (asynchronous >= 0 && (end < 0 || asynchronous < end))
        {
            end = asynchronous;
        }

        var alt = script.IndexOf("\nconst ", start + 1, StringComparison.Ordinal);
        if (alt >= 0 && (end < 0 || alt < end))
        {
            end = alt;
        }

        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void TheCard_IsFilledFromTheListing_AndAnOlderServiceIsNotAnError()
    {
        var refresh = FunctionBody(AppJs(), "refreshRestoreReceipts");

        Assert.Contains("list_restore_receipts", refresh, StringComparison.Ordinal);
        Assert.Contains("\"restore_receipts\"", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("run({ command: \"list_restore_receipts\"", refresh, StringComparison.Ordinal,
            "a service before 1.73 refuses the verb; that must not toast on every visit");
        Assert.Contains("limit: RESTORE_RECEIPTS_SHOWN", refresh, StringComparison.Ordinal,
            "the limit reaches the service, which reads no more than it shows");
        Assert.Contains("RESTORE_RECEIPTS_SHOWN = 50", AppJs(), StringComparison.Ordinal);
        Assert.Contains("listed.total", refresh, StringComparison.Ordinal, "how many are kept arrives beside the rows");
    }

    [TestMethod]
    public void TheCard_IsFetchedOnEnteringTheView_AndNeverByThePollers()
    {
        var script = AppJs();
        Assert.Contains("refreshRestoreReceipts()", FunctionBody(script, "route"), StringComparison.Ordinal);
        Assert.DoesNotContain("refreshRestoreReceipts", FunctionBody(script, "boot"), StringComparison.Ordinal,
            "what earlier restores did is read on a visit, not a heartbeat");
        Assert.Contains("renderRestoreReceiptsCard()", FunctionBody(script, "renderMaintenance"), StringComparison.Ordinal);
    }

    [TestMethod]
    public void WhatARunDidNotRestore_IsAskedOnlyThroughAnUnlockedSource_ClosedAfter()
    {
        var script = AppJs();
        var start = script.IndexOf("async \"restore-receipt-files\"(el)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "app.js no longer declares the action");
        var end = script.IndexOf("\n  },", start, StringComparison.Ordinal);
        var action = script[start..end];

        Assert.Contains("unlockSource(", action, StringComparison.Ordinal, "a receipt's files are the backup's file names");
        Assert.Contains("command: \"get_restore_receipt\"", action, StringComparison.Ordinal);
        Assert.Contains("source,", action, StringComparison.Ordinal, "asked through the source the passphrase unlocked");
        Assert.Contains("finally", action, StringComparison.Ordinal);
        Assert.Contains("close_restore_source", action, StringComparison.Ordinal, "and the source closed after");
    }

    [TestMethod]
    public void ARowTheServiceCouldNotRead_SaysSo()
    {
        var card = FunctionBody(AppJs(), "renderRestoreReceiptsCard");

        Assert.Contains("row.problem", card, StringComparison.Ordinal);
        Assert.Contains("unreadable", card, StringComparison.Ordinal);
        Assert.Contains("S.restoreReceipts === null", card, StringComparison.Ordinal, "not yet fetched is not empty");
        Assert.DoesNotContain("style=", card, StringComparison.Ordinal, "inline style is refused by the CSP");
    }
}
