namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's "Replicas stored here" control, pinned structurally
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// §3, FR-REP-001): the table is built from <c>list_replica_attributions</c>,
/// the Re-point control renders for the Owner alone and only on a replica
/// its owner cannot claim with the passphrase, the dialog offers only
/// devices that store here behind a confirm word, and the go-handler sends
/// <c>reattribute_replica</c> and nothing else. A claim held for this
/// machine's operator (FR-DR-005) shows on its row, and its acknowledgement
/// renders beside the row and beside the claim's notice, for the Owner
/// alone, behind a confirm word, sending <c>acknowledge_replica_claim</c>.
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

    [TestMethod]
    public void AHeldClaim_ShowsOnItsRow_AndItsAcknowledgementIsForTheOwnerAlone()
    {
        // FR-DR-005: a replica a claim moved is held — readable by the
        // claimant, not deletable — until this machine's operator
        // acknowledges the claim. The row says so, and the acknowledgement
        // sits beside it.
        var body = FunctionBody(AppJs(), "renderConfigBody");

        Assert.Contains("row.claimAwaitingAcknowledgement ?", body, StringComparison.Ordinal,
            "a held claim must show on its row, where the operator looks at what is stored here");
        Assert.Contains("data-action=\"claim-ack-open\"", body, StringComparison.Ordinal);
        Assert.Contains("signedInRole === \"Owner\" && row.claimAwaitingAcknowledgement", body, StringComparison.Ordinal,
            "the service refuses anyone but the Owner, so the button renders for the Owner alone, and only on a "
            + "claim that is held");
    }

    [TestMethod]
    public void TheClaimsNotice_CarriesTheAcknowledgement_BesideItsOwnDismissal()
    {
        // The notice a claim raises asks for a decision, and its own
        // Acknowledge only dismisses it — so the decision renders beside it,
        // where the operator reads the question.
        var body = FunctionBody(AppJs(), "renderNotices");

        Assert.Contains("notice.key?.startsWith(\"replica-claimed:\")", body, StringComparison.Ordinal);
        Assert.Contains("data-action=\"claim-ack-open\"", body, StringComparison.Ordinal);
        Assert.Contains("S.signedInRole === \"Owner\" && notice.key?.startsWith(\"replica-claimed:\")", body,
            StringComparison.Ordinal, "the same Owner-only rule as the row's button");
    }

    [TestMethod]
    public void TheAcknowledgement_IsTyped_AndSaysWhatItReleases()
    {
        var open = ActionBody(AppJs(), "claim-ack-open");

        Assert.Contains("data-word=\"acknowledge\"", open, StringComparison.Ordinal,
            "letting whoever proved the passphrase delete from somebody's last copy is typed, never one-clicked");
        Assert.Contains("data-enables=\"claim-ack-go\"", open, StringComparison.Ordinal);
        Assert.Contains("delete", open, StringComparison.Ordinal,
            "the dialog must say that deleting is what acknowledging releases");
        Assert.Contains("passphrase may be in the wrong hands", open, StringComparison.Ordinal,
            "the dialog must say what a claim nobody expected means, and that cancelling is the answer to it");
    }

    [TestMethod]
    public void TheAcknowledgementGo_SendsTheVerbAndNothingElse()
    {
        var go = ActionBody(AppJs(), "claim-ack-go");

        Assert.Contains("command: \"acknowledge_replica_claim\"", go, StringComparison.Ordinal);
        Assert.Contains("repositoryId:", go, StringComparison.Ordinal);
        Assert.DoesNotContain("acknowledge_notice", go, StringComparison.Ordinal,
            "the service resolves the claim's notice itself; the console dismissing it too would be a second write");
    }
}
