namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's snapshot row says how far the capturing machine's clock
/// stood from its peer's (NFR-TIME-002, contract 1.47), pinned structurally:
/// the row draws the reading the service sent and nothing where there is
/// none, the direction is named relative to the peer, and a skew of five
/// minutes or more is drawn as a warning.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleReceiptsScriptTests"/>: no browser, the script's own
/// text, failing with the reason spelled out. The DOM suite drives the same
/// row in a browser.
/// </remarks>
[TestClass]
public sealed class ConsoleSnapshotClockScriptTests
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
    public void TheRow_DrawsTheReadingTheServiceSent()
    {
        var row = FunctionBody(AppJs(), "renderSnapshots");

        Assert.Contains(
            "clockSkew(s.observedClockSkewMs)", row, StringComparison.Ordinal,
            "the reading arrives as observedClockSkewMs through the camelCase relay");
    }

    [TestMethod]
    public void NoReading_DrawsNothing_AndIsNotAClockInStep()
    {
        var clock = FunctionBody(AppJs(), "clockSkew");

        Assert.Contains(
            "ms == null", clock, StringComparison.Ordinal,
            "a capture with no peer, and a service older than 1.47, draw nothing rather than a clock in step");
        Assert.Contains("in step with its peer", clock, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheDirection_IsRelativeToThePeer_AndFiveMinutesIsAWarning()
    {
        var clock = FunctionBody(AppJs(), "clockSkew");

        Assert.Contains("behind its peer", clock, StringComparison.Ordinal);
        Assert.Contains("ahead of its peer", clock, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "slow", clock, StringComparison.Ordinal,
            "a disagreement between two clocks does not say which is wrong");
        Assert.Contains("2000", clock, StringComparison.Ordinal, "under two seconds is in step");
        Assert.Contains("300000", clock, StringComparison.Ordinal, "from five minutes the reading is a warning");
        Assert.Contains("b class=\"warn\"", clock, StringComparison.Ordinal);
    }
}
