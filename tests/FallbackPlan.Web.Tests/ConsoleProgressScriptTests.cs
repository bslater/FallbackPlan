namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's live-progress arithmetic and lifecycle, pinned structurally
/// (FR-SVC-006): the meter divides by the run's counted plan, the estimate
/// is derived and displayed, the overview shows a live job, and the event
/// stream sleeps with the tab so a backgrounded console holds no service
/// subscription its own pollers have abandoned.
/// </summary>
/// <remarks>
/// Like <see cref="SetupWizardScriptTests"/>: no browser, just the script's
/// own text, failing with the reason spelled out. The old ratio divided
/// files-handled by a "seen" figure that was the same wire field — pinned at
/// 100% for every run — and added the reused count on top of the done count
/// it is a subset of; these pins keep both mistakes dead.
/// </remarks>
[TestClass]
public sealed class ConsoleProgressScriptTests
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
    /// declaration — coarse, and enough: the page's functions are declared
    /// at column zero.
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
    public void TheJobMeter_DividesTheHandledCountByTheCountedPlan()
    {
        var script = AppJs();
        var card = FunctionBody(script, "renderLiveJob");

        Assert.Contains("totalFiles", card, StringComparison.Ordinal,
            "the meter's denominator is the run's counted plan, not a moving tally");
        Assert.Contains(
            "const handled = (progress?.filesDone ?? 0) + (progress?.filesFailed ?? 0)",
            card,
            StringComparison.Ordinal,
            "handled is done plus failed — reused is a subset of done and must not be added again");
    }

    [TestMethod]
    public void TheCountingPhase_IsNamedWhileThePlanIsStillOpen()
    {
        // Before the totals land the run is walking the source to count it;
        // the card says so instead of showing a meaningless bar.
        var card = FunctionBody(AppJs(), "renderLiveJob");
        Assert.Contains("Counting files", card, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheEstimate_IsDerivedFromThePlanAndShownOnTheCard()
    {
        var script = AppJs();
        var estimator = FunctionBody(script, "jobEta");

        Assert.Contains("totalFiles", estimator, StringComparison.Ordinal,
            "the estimate needs the plan for a remaining-work figure");
        Assert.Contains("jobEta(", FunctionBody(script, "renderLiveJob"), StringComparison.Ordinal,
            "the jobs card shows the estimate");
    }

    [TestMethod]
    public void TheOverviewCard_ShowsTheSetsLiveJobProgress()
    {
        var card = FunctionBody(AppJs(), "renderSetCard");
        Assert.Contains("S.progress", card, StringComparison.Ordinal,
            "the overview must render the live run's progress, not only disable its buttons");
    }

    [TestMethod]
    public void TheEventStream_SleepsWhileTheTabIsHidden()
    {
        // A hidden tab's pollers already pause; the SSE stream must pause
        // with them, or a backgrounded console keeps a service watch — and
        // therefore a hub subscription — alive that nobody is reading.
        var script = AppJs();
        var handler = FunctionBody(script, "applyVisibility");

        Assert.Contains("disconnectEvents()", handler, StringComparison.Ordinal,
            "hiding the tab must close the event stream");
        Assert.Contains("connectEvents()", handler, StringComparison.Ordinal,
            "showing the tab must reopen it");
        Assert.Contains("applyVisibility", script[script.IndexOf("visibilitychange", StringComparison.Ordinal)..], StringComparison.Ordinal,
            "the visibilitychange listener must run the lifecycle");
    }
}
