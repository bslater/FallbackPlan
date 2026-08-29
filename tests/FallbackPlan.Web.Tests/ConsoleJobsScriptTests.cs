namespace FallbackPlan.Web.Tests;

/// <summary>
/// The completed-job drill-down on the Jobs view (ADR-0050), pinned
/// structurally: history rows open a report, the report summarises the run
/// from the row's own stats joined to its snapshot, and the details —
/// what changed, what failed — are asked of the service on demand.
/// </summary>
[TestClass]
public sealed class ConsoleJobsScriptTests
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
    public void HistoryRows_AreClickable_AndOpenTheJobReport()
    {
        var jobs = FunctionBody(AppJs(), "renderJobs");

        Assert.Contains("rowlink", jobs, StringComparison.Ordinal,
            "a settled row that cannot be opened hides everything the run recorded");
        Assert.Contains("data-action-row=\"job-details\"", jobs, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheHistoryAsk_IsBounded()
    {
        var refresh = FunctionBody(AppJs(), "refreshJobs");

        // The journal grows for the life of the installation; an unbounded
        // ask eventually exceeds what one frame may carry (contract 1.22).
        Assert.Contains("limit", refresh, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheJobReport_SummarisesTheRunFromItsOwnNumbers()
    {
        var report = FunctionBody(AppJs(), "jobReport");

        foreach (var fact in new[] { "filesDone", "filesReused", "filesFailed", "bytesStored" })
        {
            Assert.Contains(fact, report, StringComparison.Ordinal,
                $"the summary no longer states {fact} — the row records it precisely so the console can");
        }

        // Duration is derived from the row's own two timestamps, not invented.
        Assert.Contains("startedAt", report, StringComparison.Ordinal);
        Assert.Contains("updatedAt", report, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheJobDetailsAction_JoinsTheSnapshot_AndOffersTheBrowser()
    {
        var details = ActionBody(AppJs(), "job-details");

        Assert.Contains("S.snapshots", details, StringComparison.Ordinal,
            "the summary joins the row to its committed snapshot for capture facts");
        Assert.Contains("data-action=\"browse\"", details, StringComparison.Ordinal,
            "the full contents are one click away through the existing snapshot browser");
    }

    [TestMethod]
    public void TheJobDetailsDialog_OffersTheOnDemandDetails()
    {
        var details = ActionBody(AppJs(), "job-details");

        Assert.Contains("data-action=\"job-changes\"", details, StringComparison.Ordinal,
            "what the run changed is one click deeper — the service diffs it on demand");
        Assert.Contains("data-action=\"job-failures\"", details, StringComparison.Ordinal,
            "what the run could not read is one click deeper — the error manifest names it");
    }

    [TestMethod]
    public void TheJobChangesAction_SendsTheVerb_AndRendersTheBuckets()
    {
        var script = AppJs();
        var action = ActionBody(script, "job-changes");

        Assert.Contains("job_changes", action, StringComparison.Ordinal);
        Assert.Contains("jobChangesReport", action, StringComparison.Ordinal);

        var report = FunctionBody(script, "jobChangesReport");
        foreach (var bucket in new[] { "new", "changed", "removed" })
        {
            Assert.Contains($"result.{bucket}", report, StringComparison.Ordinal,
                $"the report no longer renders the '{bucket}' bucket");
        }

        Assert.Contains("… and", report, StringComparison.Ordinal,
            "counts are exact while samples are bounded — the report must say when it is showing a sample");
    }

    [TestMethod]
    public void TheJobFailuresAction_SendsTheVerb_AndNamesReasons()
    {
        var action = ActionBody(AppJs(), "job-failures");

        Assert.Contains("job_failures", action, StringComparison.Ordinal);
        Assert.Contains("reason", action, StringComparison.Ordinal,
            "each failure carries its typed reason beside the path");
    }
}
