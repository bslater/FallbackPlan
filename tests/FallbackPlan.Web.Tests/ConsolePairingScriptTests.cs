namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's "Replicas stored here" control, pinned structurally
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// §3, FR-REP-001): the table is built from <c>list_replica_attributions</c>,
/// the Re-point control renders for the Owner alone and only on a replica
/// its owner cannot claim with the passphrase, the dialog offers only
/// devices that store here behind a confirm word, and the go-handler sends
/// <c>reattribute_replica</c> and nothing else.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleAdminScriptTests"/>: no browser, the script's own
/// text, failing with the reason spelled out.
/// </remarks>
[TestClass]
public sealed class ConsolePairingScriptTests
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
    public void TheTable_IsBuiltFromTheListing_AndAnOlderServiceIsNotAnError()
    {
        var refresh = FunctionBody(AppJs(), "refreshConfigData");

        Assert.Contains("list_replica_attributions", refresh, StringComparison.Ordinal);
        Assert.Contains("replica_attributions", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("run({ command: \"list_replica_attributions\"", refresh, StringComparison.Ordinal,
            "a service that predates 1.31 refuses the verb; that must not toast on every refresh");
    }

    [TestMethod]
    public void TheRePointControl_IsForTheOwnerAlone_AndOnlyWhereThePassphraseCannotClaim()
    {
        var body = FunctionBody(AppJs(), "renderConfigBody");

        Assert.Contains("data-action=\"reattribute-open\"", body, StringComparison.Ordinal);
        Assert.Contains("signedInRole === \"Owner\" && row.claimable === false", body, StringComparison.Ordinal,
            "an operator would only be refused, and a claimable replica is its owner's to claim — the button "
            + "renders for the Owner alone and never on a replica that carries a claim key");
        Assert.Contains("Replicas stored here", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheDialog_OffersOnlyDevicesThatStoreHere_BehindTheConfirmWord()
    {
        var open = ActionBody(AppJs(), "reattribute-open");

        Assert.Contains("stores-for-us", open, StringComparison.Ordinal,
            "a destination we store at holds nothing here and is not offered");
        Assert.Contains("data-word=\"re-point\"", open, StringComparison.Ordinal,
            "handing somebody's backup to a device is typed, never one-clicked");
        Assert.Contains("data-enables=\"reattribute-go\"", open, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheGo_SendsTheVerbAndNothingElse()
    {
        var go = ActionBody(AppJs(), "reattribute-go");

        Assert.Contains("command: \"reattribute_replica\"", go, StringComparison.Ordinal);
        Assert.Contains("repositoryId:", go, StringComparison.Ordinal);
        Assert.Contains("fingerprint:", go, StringComparison.Ordinal);
        Assert.DoesNotContain("unpair", go, StringComparison.Ordinal);
    }
}
