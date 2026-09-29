namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's background-limits clause, pinned structurally (contract
/// 1.43, NFR-PERF-013): the overview says which byte rates background work is
/// held to — the source-read limit and each destination's transfer limit —
/// drawn from the status result the view already holds, and says nothing at
/// all when nothing is limited.
/// </summary>
/// <remarks>
/// <para>
/// The absent case is the one worth a test of its own, for the reason the
/// window's is: null arrives from a service with no limit configured and from
/// any service older than 1.43, and both mean draw no line. A clause that
/// rendered "undefined" would be a regression on every installation at once.
/// </para>
/// <para>
/// Like <see cref="ConsoleBackgroundWindowScriptTests"/>: no browser, the
/// script's own text, failing with the reason spelled out. Does not establish
/// FR-SVC-006.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsoleBackgroundLimitsScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    private static string LimitsNoteBody()
    {
        var script = AppJs();
        var start = script.IndexOf("function limitsNote()", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "app.js declares no limitsNote");
        var end = script.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void TheOverview_DrawsTheLimitsClauseFromTheStatusItAlreadyHolds()
    {
        var script = AppJs();

        Assert.Contains(
            "function limitsNote()",
            script,
            StringComparison.Ordinal,
            "the limits clause must be one named function, not inlined into two subtitles");
        Assert.Contains(
            "S.status?.backgroundLimits",
            script,
            StringComparison.Ordinal,
            "the clause reads the polled status, so it costs no second round trip");

        // Both subtitles carry it, beside the window: a slow sync on an
        // installation with no sets yet is exactly the one being asked about.
        var uses = script.Split("${limitsNote()}", StringSplitOptions.None).Length - 1;
        Assert.AreEqual(2, uses, "both overview subtitles should carry the clause");
    }

    [TestMethod]
    public void TheClause_IsSilentWhenNothingIsLimited()
    {
        Assert.Contains(
            "if (!l) return \"\";",
            LimitsNoteBody(),
            StringComparison.Ordinal,
            "no limits must render nothing — not 'undefined', and not an empty clause");
    }

    [TestMethod]
    public void TheClause_NamesEachDestinationAndEscapesWhatItShows()
    {
        var body = LimitsNoteBody();

        Assert.Contains("l.readLimit", body, StringComparison.Ordinal);
        Assert.Contains("l.transferLimits", body, StringComparison.Ordinal);
        Assert.Contains("destinationName", body, StringComparison.Ordinal);

        // A destination's name is the operator's text; it reaches the page
        // escaped like every other name the console shows.
        Assert.Contains("esc(", body, StringComparison.Ordinal);
    }
}
