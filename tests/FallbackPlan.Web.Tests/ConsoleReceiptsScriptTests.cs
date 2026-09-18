namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's Receipts card, pinned structurally (contract 1.33;
/// [ADR-0064](../../docs/adr/0064-replication-receipts.md), FR-GC-008,
/// FR-DEST-004): the card is filled from <c>list_receipts</c> taken by its
/// discriminator so an older service reads as an empty card rather than a
/// toast; it is fetched on entering the Maintenance view and never by the
/// second-by-second pollers; and each row's status is the service's own
/// verdict, rendered as one of three states — verified, signature invalid,
/// unreadable — never derived on the page from the absence of a problem.
/// </summary>
/// <remarks>
/// Like <see cref="ConsolePairingScriptTests"/>: no browser, the script's own
/// text, failing with the reason spelled out.
/// </remarks>
[TestClass]
public sealed class ConsoleReceiptsScriptTests
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
    public void TheCard_IsFilledFromTheListing_AndAnOlderServiceIsNotAnError()
    {
        var refresh = FunctionBody(AppJs(), "refreshReceipts");

        Assert.Contains("list_receipts", refresh, StringComparison.Ordinal);
        Assert.Contains("receipts_listed", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("run({ command: \"list_receipts\"", refresh, StringComparison.Ordinal,
            "a service that predates 1.33 refuses the verb; that must not toast on every visit");
        Assert.Contains("limit: 50", refresh, StringComparison.Ordinal, "the card shows the newest fifty");
    }

    [TestMethod]
    public void TheCard_IsFetchedOnEnteringTheView_AndNeverByThePollers()
    {
        var script = AppJs();
        var route = FunctionBody(script, "route");
        Assert.Contains("refreshReceipts()", route, StringComparison.Ordinal);
        Assert.Contains("S.view === \"maintenance\"", route, StringComparison.Ordinal);

        var pollers = FunctionBody(script, "boot");
        Assert.Contains("setInterval", pollers, StringComparison.Ordinal, "the pollers moved; find them again");
        Assert.DoesNotContain("refreshReceipts", pollers, StringComparison.Ordinal,
            "receipts are an audit listing read on a visit, not a heartbeat");

        var maintenance = FunctionBody(script, "renderMaintenance");
        Assert.Contains("renderReceiptsCard()", maintenance, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EachRow_RendersTheServicesVerdict_InOneOfThreeStates()
    {
        var card = FunctionBody(AppJs(), "renderReceiptsCard");

        Assert.Contains("row.status", card, StringComparison.Ordinal);
        Assert.Contains("\"verified\"", card, StringComparison.Ordinal);
        Assert.Contains("\"signature-invalid\"", card, StringComparison.Ordinal);
        Assert.Contains("\"unreadable\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("row.problem ==", card, StringComparison.Ordinal,
            "the status is the service's verdict on the bytes on disk, not the absence of a problem string");
        Assert.DoesNotContain("row.verified ?", card, StringComparison.Ordinal,
            "two states from a boolean would fold 'unreadable' into 'signature invalid'");
        Assert.Contains("S.receipts === null", card, StringComparison.Ordinal, "not yet fetched is not empty");
        Assert.DoesNotContain("style=", card, StringComparison.Ordinal, "inline style is refused by the CSP");
    }
}
