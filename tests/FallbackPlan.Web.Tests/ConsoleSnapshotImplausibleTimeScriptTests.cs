namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's snapshot row says when a snapshot's capture time does not
/// fit the order its writer published it in (FR-GC-012, contract 1.48). This
/// pins it structurally.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The row draws the finding the service sent, and nothing where there is none.</item>
/// <item>It names the way the capture is out of step.</item>
/// <item>It draws that as a warning, because retention is keeping the snapshot on account of it.</item>
/// </list>
/// <para>
/// Like <see cref="ConsoleSnapshotClockScriptTests"/>: no browser, the
/// script's own text, and a failure that spells out the reason. The DOM suite
/// drives the same row in a browser.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsoleSnapshotImplausibleTimeScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    private static string FunctionBody(string script, string name)
    {
        var start = script.IndexOf($"function {name}", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"app.js no longer declares '{name}'");

        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void TheRow_DrawsTheFindingTheServiceSent()
    {
        var row = FunctionBody(AppJs(), "renderSnapshots");

        Assert.Contains(
            "implausibleCaptureTime(s.implausibleCaptureTime)", row, StringComparison.Ordinal,
            "the finding arrives as implausibleCaptureTime through the camelCase relay");
    }

    [TestMethod]
    public void NoFinding_DrawsNothing()
    {
        var finding = FunctionBody(AppJs(), "implausibleCaptureTime");

        Assert.Contains(
            "direction == null", finding, StringComparison.Ordinal,
            "a capture that fits, and a service older than 1.48, draw nothing");
    }

    [TestMethod]
    public void TheFinding_NamesItsDirection_AsAWarning()
    {
        var finding = FunctionBody(AppJs(), "implausibleCaptureTime");

        Assert.Contains("dated before snapshots taken ahead of it", finding, StringComparison.Ordinal);
        Assert.Contains("dated after snapshots taken after it", finding, StringComparison.Ordinal);
        Assert.Contains("kept, never expired", finding, StringComparison.Ordinal);
        Assert.Contains("b class=\"warn\"", finding, StringComparison.Ordinal);
    }
}
