namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's snapshot view is one timeline, newest first across every
/// set, pinned structurally. <c>list_snapshots</c> answers each set newest
/// first, one set after another; the view reversed that list, which drew
/// each set's oldest capture on top.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleSnapshotClockScriptTests"/>: no browser, the
/// script's own text. The DOM suite drives the same view in a browser.
/// </remarks>
[TestClass]
public sealed class ConsoleSnapshotOrderScriptTests
{
    private static string FunctionBody(string script, string name)
    {
        var start = script.IndexOf($"function {name}", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"app.js no longer declares '{name}'");

        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void TheView_SortsByCaptureTime_RatherThanReversingTheServicesOrder()
    {
        var view = FunctionBody(SetupWizardScriptTests.AppJs(), "renderSnapshots");

        Assert.DoesNotContain(
            ".reverse()", view, StringComparison.Ordinal,
            "list_snapshots is already newest first within each set; reversing it puts the oldest capture on top");
        Assert.Contains(
            "Number(b.capturedAt) - Number(a.capturedAt)", view, StringComparison.Ordinal,
            "the newest capture of any set comes first");
    }
}
