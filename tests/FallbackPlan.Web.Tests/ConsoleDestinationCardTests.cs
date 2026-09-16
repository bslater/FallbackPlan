namespace FallbackPlan.Web.Tests;

/// <summary>
/// The overview's destination cards, pinned structurally: a completion ring
/// per destination, read from the service's own figures (contract 1.24) and
/// never re-derived, with the uncounted case drawn as uncounted rather than
/// as empty.
/// </summary>
/// <remarks>
/// Like <see cref="ConsoleProgressScriptTests"/>: no browser, just the
/// script's own text, failing with the reason spelled out. The mistake these
/// exist to keep dead is the one the wire field <c>measured_at</c> was added
/// for — a destination no pass has reached holds an unknown amount, and a
/// ring drawn at zero over it tells the person their backup is not there.
/// </remarks>
[TestClass]
public sealed class ConsoleDestinationCardTests
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

    [TestMethod]
    public void Completion_AnUncountedDestination_IsNullRatherThanZero()
    {
        var body = FunctionBody(AppJs(), "destCompletion");

        Assert.Contains("measuredAt == null", body, StringComparison.Ordinal);
        Assert.Contains("return null", body, StringComparison.Ordinal);
        Assert.IsFalse(
            body.Contains("measuredAt == null) return 0", StringComparison.Ordinal),
            "an uncounted destination must not read as zero per cent — that claims it holds nothing");
    }

    [TestMethod]
    public void Completion_IsTheServicesFigures_NotAReDerivation()
    {
        var body = FunctionBody(AppJs(), "destCompletion");

        // The console renders the service's answer and never re-derives one
        // beside it (ADR-0028 §8). Held over owed is arithmetic on two wire
        // fields, which is the rendering; anything reaching for object counts
        // or the job feed here would be a second opinion.
        Assert.Contains("d.heldBytes / d.owedBytes", body, StringComparison.Ordinal);
        Assert.IsFalse(body.Contains("S.progress", StringComparison.Ordinal),
            "the standing completion figure must not be taken from the live job feed");
    }

    [TestMethod]
    public void Completion_ADestinationOwedNothing_IsComplete()
    {
        var body = FunctionBody(AppJs(), "destCompletion");

        // A counted destination that is owed nothing holds all of nothing.
        // Dividing by zero here would render NaN into the page.
        Assert.Contains("owedBytes > 0", body, StringComparison.Ordinal);
        Assert.Contains("return 100", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Ring_WithNoFigure_DrawsTheTrackAloneAndNoArc()
    {
        var body = FunctionBody(AppJs(), "ring");

        Assert.Contains("unknown", body, StringComparison.Ordinal);
        Assert.Contains("ring-track", body, StringComparison.Ordinal);

        // The arc element itself is conditional: an unknown ring has no
        // ring-fill to offset, so it cannot be mistaken for a full circle
        // with a zero-length arc.
        Assert.Contains("known ?", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Ring_TheArcLength_IsAppliedFromScriptNotAnInlineStyle()
    {
        var script = AppJs();

        // The page forbids inline style attributes, which is why the meters'
        // widths are applied in the render pass; the ring follows the same
        // route rather than inventing a second one.
        Assert.Contains("arc.style.strokeDashoffset", script, StringComparison.Ordinal);
        Assert.IsFalse(
            FunctionBody(script, "ring").Contains("style=", StringComparison.Ordinal),
            "the ring markup must carry no inline style attribute");
    }

    [TestMethod]
    public void Card_TheLiveOverlay_IsOnlyForSetsThatShipStraightToDestinations()
    {
        var body = FunctionBody(AppJs(), "renderSetCard");

        // A staging set's live job is a capture, not a transfer. Showing its
        // progress on a destination card would credit that destination with
        // work that has not started reaching it.
        Assert.Contains("config?.directShip", body, StringComparison.Ordinal);
        Assert.Contains("destCompletion(d)", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Drill_NeverDrilled_ReadsAsNeitherPassedNorFailed()
    {
        var body = FunctionBody(AppJs(), "drillLabel");

        // Three states, three answers (contract 1.25). "Never drilled" and
        // "could not restore" both mean this destination has not been shown
        // to work, and only the second means something is wrong — a label
        // that folded "never" into either would make an unexercised
        // destination look like an exercised one.
        Assert.Contains("drilledAt == null", body, StringComparison.Ordinal);
        Assert.Contains("never drilled", body, StringComparison.Ordinal);
        Assert.Contains("d.drillFailure", body, StringComparison.Ordinal);
        Assert.Contains("could not restore", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Drill_APassWithAStatedLimit_IsNeitherAFailureNorAPlainPass()
    {
        var body = FunctionBody(AppJs(), "drillLabel");

        // Contract 1.27: a write-only set's drill proves the road back as far
        // as the sealed content. The label says exactly that — not "restored",
        // which it did not do, and not "could not restore", which it did not
        // find — and the limit's own words go through the escape.
        Assert.Contains("d.drillLimit", body, StringComparison.Ordinal);
        Assert.Contains("proved to the sealed content", body, StringComparison.Ordinal);
        Assert.Contains("esc(d.drillLimit)", body, StringComparison.Ordinal);
        Assert.IsTrue(
            body.IndexOf("d.drillFailure", StringComparison.Ordinal) < body.IndexOf("d.drillLimit", StringComparison.Ordinal),
            "a failure outranks a limit: a drill that failed says so whatever it also could not prove");
    }

    [TestMethod]
    public void Drill_TheFailureText_ComesFromTheServiceAndIsEscaped()
    {
        var body = FunctionBody(AppJs(), "drillLabel");

        // The drill's own words reach the page, so they go through the
        // escape like every other service string — a restore failure detail
        // can carry a captured file's path, which is attacker-influenced
        // content by definition.
        Assert.Contains("esc(d.drillFailure)", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Drill_TheRow_IsOnTheDestinationBodyBesidePossession()
    {
        var body = FunctionBody(AppJs(), "renderSetCard");

        // Beside Possession rather than in the glance: a drill age is a fact
        // somebody checks, not one they scan. The card's caption line stays
        // about what the destination holds now.
        Assert.Contains("Restore drill", body, StringComparison.Ordinal);
        Assert.Contains("drillLabel(d)", body, StringComparison.Ordinal);

        // Below the expand, not in it: in the rendered markup the drill row
        // comes after the caption, which stays about what the destination
        // holds right now.
        Assert.IsTrue(
            body.IndexOf("dest-caption", StringComparison.Ordinal)
                < body.IndexOf("drillLabel(d)", StringComparison.Ordinal),
            "the drill row belongs in the destination body, under the glance");
    }
}
