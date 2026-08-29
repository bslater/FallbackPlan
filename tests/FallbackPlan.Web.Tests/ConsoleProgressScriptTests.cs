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
    public void TheCurrentFile_IsShownOnTheCard_TruncatedInTheMiddle()
    {
        var script = AppJs();
        var card = FunctionBody(script, "renderLiveJob");

        Assert.Contains("currentFile", card, StringComparison.Ordinal,
            "the card no longer names the file being processed (contract 1.22)");
        Assert.Contains("truncateMiddle(", card, StringComparison.Ordinal,
            "a long path is truncated in the middle — the volume and the leaf survive, the middle gives way");

        var truncate = FunctionBody(script, "truncateMiddle");
        Assert.Contains("…", truncate, StringComparison.Ordinal);
        Assert.Contains("title=", card, StringComparison.Ordinal,
            "the full path stays reachable — hover shows what the truncation hid");
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
    public void TheThroughput_IsDerivedAndShownOnTheJobsCard()
    {
        var script = AppJs();
        var rate = FunctionBody(script, "jobRate");

        Assert.Contains("byteRate", rate, StringComparison.Ordinal,
            "throughput is bytes-per-second while content moves");
        Assert.Contains("files/s", rate, StringComparison.Ordinal,
            "a reuse-heavy run moves no bytes; files-per-second is its honest rate");
        Assert.Contains("jobRate(", FunctionBody(script, "renderLiveJob"), StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheDestinationReason_IsRenderedWithItsTimestamps()
    {
        var card = FunctionBody(AppJs(), "renderSetCard");

        // The service carries the cause (contract 1.22); the console renders
        // its words and, for the self-healing catch-up window, the two
        // timestamps the demotion compared — presentation only, never a
        // re-derivation (ADR-0028 §8).
        Assert.Contains("d.reason", card, StringComparison.Ordinal,
            "the destination row no longer renders the machine cause");
        Assert.Contains("catching-up", card, StringComparison.Ordinal);
        Assert.Contains("set.lastCompletedAt", card, StringComparison.Ordinal,
            "the catch-up note names the backup the destination is behind");
    }

    [TestMethod]
    public void TheCatchingUpChip_ReadsAsActivityNotAlarm()
    {
        var card = FunctionBody(AppJs(), "renderSetCard");

        // The chip choice keys off the wire reason — rendering the service's
        // answer, never re-deriving (ADR-0028 §8). Any other behind keeps
        // the warn chip.
        Assert.Contains("d.reason === \"catching-up\"", card, StringComparison.Ordinal);
        Assert.Contains("syncing", card, StringComparison.Ordinal,
            "the catch-up window is activity in progress, not an alarm");
        Assert.Contains("accent", card, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheOverviewDestinations_StackVerticallyInShipOrder()
    {
        var card = FunctionBody(AppJs(), "renderSetCard");

        Assert.Contains("details class=\"dest\"", card, StringComparison.Ordinal,
            "destinations are vertically stacked collapsible boxes, not a horizontal table");
        Assert.Contains("destPriority", card, StringComparison.Ordinal,
            "the stack orders by priority — the order backups ship (ADR-0047)");
        Assert.Contains("S.openDests", card, StringComparison.Ordinal,
            "an opened box must survive the overview's frequent re-renders");
    }

    [TestMethod]
    public void TheOverviewSets_StackVerticallyAndCollapse()
    {
        var script = AppJs();
        var overview = FunctionBody(script, "renderOverview");
        var card = FunctionBody(script, "renderSetCard");

        // The destinations' own pattern, one level up: sets are a single
        // vertical stack of collapsible rows, not a grid of always-open
        // cards — a collapsed stack reads name and status at a glance.
        Assert.Contains("set-stack", overview, StringComparison.Ordinal,
            "sets stack vertically, full width");
        Assert.DoesNotContain("cols-2", overview, StringComparison.Ordinal,
            "the two-column card grid is gone from the overview");
        Assert.Contains("details class=\"set\"", card, StringComparison.Ordinal,
            "each set is a collapsible row");
        Assert.Contains("S.openSets", card, StringComparison.Ordinal,
            "an opened set must survive the overview's frequent re-renders");
    }

    [TestMethod]
    public void TheCollapsedSetSummary_CarriesNameStatusAndLiveProgressOnly()
    {
        // Sliced from the set-level row's own template — the destination
        // boxes earlier in the function have summaries of their own.
        var card = FunctionBody(AppJs(), "renderSetCard");
        var row = card[card.IndexOf("details class=\"set\"", StringComparison.Ordinal)..];
        var summary = row[row.IndexOf("<summary>", StringComparison.Ordinal)
            ..row.IndexOf("</summary>", StringComparison.Ordinal)];
        var body = row[row.IndexOf("</summary>", StringComparison.Ordinal)..];

        // Clean by construction: the summary line is the set's name, its
        // protection badge, and — while a run is live — a slim meter.
        // Everything else (roots, destinations, warnings, actions) waits
        // behind the expand.
        Assert.Contains("badge(meta", summary, StringComparison.Ordinal,
            "the collapsed row must still show the protection status");
        Assert.Contains("set-live-mini", summary, StringComparison.Ordinal,
            "a running backup shows on the collapsed row as a slim meter");
        Assert.DoesNotContain("actions-row", summary, StringComparison.Ordinal,
            "buttons belong to the expanded body, not the glance line");
        Assert.Contains("actions-row", body, StringComparison.Ordinal);
        Assert.Contains("${destinations}", body, StringComparison.Ordinal,
            "expanding a set is what reveals the per-destination stack");
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
