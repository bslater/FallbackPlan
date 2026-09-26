namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's background-window clause, pinned structurally (contract
/// 1.37; ADR-0069, NFR-PERF-013): the overview says whether the window is
/// open and when it next changes, drawn from the status result the view
/// already holds, and says nothing at all when there is no window.
/// </summary>
/// <remarks>
/// <para>
/// The absent case is the one worth a test of its own. Null arrives from a
/// service with no window configured and from any service older than 1.37,
/// and both mean the same instruction — draw no line. A clause that rendered
/// "undefined" or an empty window would be a regression visible on every
/// existing installation at once.
/// </para>
/// <para>
/// Like <see cref="ConsoleReceiptsScriptTests"/>: no browser, the script's
/// own text, failing with the reason spelled out. Does not establish
/// FR-SVC-006.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsoleBackgroundWindowScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    [TestMethod]
    public void TheOverview_DrawsTheWindowClauseFromTheStatusItAlreadyHolds()
    {
        var script = AppJs();

        Assert.Contains(
            "function windowNote()",
            script,
            StringComparison.Ordinal,
            "the window clause must be one named function, not inlined into two subtitles");

        Assert.Contains(
            "S.status?.backgroundWindow",
            script,
            StringComparison.Ordinal,
            "the clause reads the polled status, so it costs no second round trip");

        // Both subtitles carry it: an installation with no sets yet is
        // exactly the one whose operator is asking why nothing ran.
        var uses = script.Split("${windowNote()}", StringSplitOptions.None).Length - 1;
        Assert.AreEqual(2, uses, "both overview subtitles should carry the clause");
    }

    [TestMethod]
    public void TheClause_IsSilentWhenThereIsNoWindow()
    {
        var script = AppJs();
        var start = script.IndexOf("function windowNote()", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "app.js no longer declares windowNote");

        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? script[start..] : script[start..end];

        Assert.Contains(
            "if (!w) return \"\";",
            body,
            StringComparison.Ordinal,
            "no window must render nothing — not 'undefined', and not an empty window");
    }

    [TestMethod]
    public void TheBoundary_IsRenderedForwardsRatherThanThroughRel()
    {
        var script = AppJs();
        var start = script.IndexOf("function windowNote()", StringComparison.Ordinal);
        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? script[start..] : script[start..end];

        Assert.Contains("until(w.changesAt)", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rel(w.changesAt)",
            body,
            StringComparison.Ordinal,
            "the next change is ahead of now; rel() would render it as '-4 h ago'");
    }
}
