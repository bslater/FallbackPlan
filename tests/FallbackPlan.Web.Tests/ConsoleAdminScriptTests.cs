namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's admin surface, pinned structurally (FR-SVC-017; ADR-0049):
/// the restart control exists on the Maintenance view, only for the Owner,
/// behind the confirm-word dialog, and its go-handler sends the verb and
/// drops the browser's dead session — the restart signs everybody out by
/// design.
/// </summary>
[TestClass]
public sealed class ConsoleAdminScriptTests
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
    public void TheSetEditor_OffersTheStorageShape()
    {
        // Contract 1.23: direct-ship stops being a config-file secret. The
        // editor states the trade honestly and the payload carries the
        // explicit choice — null never leaves this editor, because an
        // explicit editor must not depend on preserve-by-omission. The
        // control lives in the "other" section's template since the editor
        // became a summary with per-section dialogs.
        var script = AppJs();
        var sections = FunctionBody(script, "setSectionHtml");

        Assert.Contains("set-direct-ship", sections, StringComparison.Ordinal,
            "the storage shape is no longer editable anywhere a user can find");
        Assert.Contains("no local staging copy", sections, StringComparison.Ordinal);
        Assert.Contains("directShip:", script, StringComparison.Ordinal,
            "the upsert payload no longer carries the choice");
    }

    [TestMethod]
    public void TheSetEditor_LandsOnASummaryWithASectionPerSetting()
    {
        // The editor opens on a summary of what is set — each setting a row
        // of plain prose with its own Change… dialog — and nothing persists
        // until the one confirm at the end.
        var script = AppJs();
        var summary = FunctionBody(script, "renderSetSummary");

        foreach (var section in new[]
        {
            "sec-name", "sec-schedule", "sec-locations", "sec-exclusions",
            "sec-destinations", "sec-retention", "sec-other",
        })
        {
            Assert.Contains($"\"{section}\"", summary, StringComparison.Ordinal,
                $"the summary must offer the {section} dialog");
        }

        Assert.Contains("describeSchedule(", summary, StringComparison.Ordinal,
            "the schedule reads as prose, not the raw grammar");
        Assert.Contains("retentionProse(", summary, StringComparison.Ordinal,
            "retention reads as prose, not raw numbers");
        Assert.Contains("set-confirm-all", summary, StringComparison.Ordinal,
            "the summary carries the confirm-all step");
        Assert.Contains("set-cancel-all", summary, StringComparison.Ordinal,
            "and the cancel that discards every pending change");
    }

    [TestMethod]
    public void TheSectionDialogs_StageIntoTheDraft_OnlyConfirmReachesTheService()
    {
        // A section's Save writes the draft and returns to the summary; the
        // service hears nothing until Confirm sends the one upsert — which
        // still runs the material-change comparison first (FR-SVC-009's
        // two-step rule).
        var script = AppJs();
        var sectionSave = ActionBody(script, "sec-save");

        Assert.Contains("E.touched", sectionSave, StringComparison.Ordinal,
            "a saved section marks the draft as pending");
        Assert.Contains("renderSetSummary(", sectionSave, StringComparison.Ordinal,
            "a saved section returns to the summary");
        Assert.DoesNotContain("applySetUpsert", sectionSave, StringComparison.Ordinal,
            "a section save must not reach the service");

        var confirm = ActionBody(script, "set-confirm-all");
        Assert.Contains("payloadFromDraft(", confirm, StringComparison.Ordinal);
        Assert.Contains("material", confirm, StringComparison.Ordinal,
            "the confirm still routes material edits through the comparison panel");
    }

    [TestMethod]
    public void TheScheduleAndRetention_ReadAsProse()
    {
        var script = AppJs();
        var schedule = FunctionBody(script, "describeSchedule");
        Assert.Contains("manual", schedule, StringComparison.Ordinal,
            "no schedule reads as the manual trigger it is");
        Assert.Contains("minute", schedule, StringComparison.Ordinal,
            "units are spelled out, not the grammar's single letters");

        var retention = FunctionBody(script, "retentionProse");
        Assert.Contains("keeps everything", retention, StringComparison.Ordinal,
            "an empty policy is said plainly");
        Assert.Contains("at least", retention, StringComparison.Ordinal,
            "the generation floor reads as a sentence");
    }

    [TestMethod]
    public void TheRestartControl_IsOnMaintenanceForTheOwnerAlone()
    {
        var maintenance = FunctionBody(AppJs(), "renderMaintenance");

        Assert.Contains("data-action=\"restart-service\"", maintenance, StringComparison.Ordinal);
        Assert.Contains("signedInRole === \"Owner\"", maintenance, StringComparison.Ordinal,
            "an operator would only be refused; the button renders for the Owner alone");
    }

    [TestMethod]
    public void TheRestart_IsBehindTheConfirmWord()
    {
        var open = ActionBody(AppJs(), "restart-service");
        Assert.Contains("data-word=\"restart\"", open, StringComparison.Ordinal,
            "a restart interrupts every run and signs everybody out — it is typed, never one-clicked");
    }

    [TestMethod]
    public void TheRestartGo_SendsTheVerbAndDropsTheDeadSession()
    {
        var go = ActionBody(AppJs(), "restart-service-go");
        Assert.Contains("restart_service", go, StringComparison.Ordinal);
        Assert.Contains("sessionExpired()", go, StringComparison.Ordinal,
            "the restart signs this browser out too (FR-USR-003); the page must not discover it refusal by refusal");
    }
}
