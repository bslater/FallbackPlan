namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's settings controls, pinned structurally (contract 1.44): a
/// service-settings card in the configuration view that reads the three
/// installation settings and writes them back, and two fields on the
/// destination form for a destination's transfer limit and drill cadence —
/// the control ADR-0069 §8 and ADR-0074 §7 named as owed.
/// </summary>
/// <remarks>
/// <para>
/// The DOM suite walks both with real clicks where a browser is provisioned;
/// these hold the same shape on every machine, from the script's own text.
/// </para>
/// <para>
/// The clearing spellings are what is worth pinning here. The card and the
/// form send every field they show, so an emptied field must leave as the
/// empty text or the zero that clears — never as null, which the service
/// reads as "keep what the file says" (FR-SVC-020), and which would make
/// clearing a limit silently do nothing.
/// </para>
/// </remarks>
[TestClass]
public sealed class ConsoleServiceSettingsScriptTests
{
    private static string AppJs() => SetupWizardScriptTests.AppJs();

    private static string ActionBody(string action)
    {
        var script = AppJs();
        var start = script.IndexOf($"async \"{action}\"(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"app.js declares no '{action}' action");
        var end = script.IndexOf("\n  },", start, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..end];
    }

    [TestMethod]
    public void TheConfigurationView_ReadsTheServiceSettings()
    {
        var script = AppJs();

        Assert.Contains("\"get_service_settings\"", script, StringComparison.Ordinal);
        Assert.Contains("function serviceSettingsCard()", script, StringComparison.Ordinal);
        foreach (var id in new[] { "svc-window", "svc-read-limit", "svc-max-backups" })
        {
            Assert.Contains($"id=\"{id}\"", script, StringComparison.Ordinal, $"the card has no {id} field");
        }

        // A width saved but not yet running is said, not hidden: the pool is
        // sized when the service starts.
        Assert.Contains("effectiveMaxConcurrentBackups", script, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheCard_SendsEveryFieldWithItsClearingSpelling()
    {
        var body = ActionBody("svc-settings-save");

        Assert.Contains("command: \"update_service_settings\"", body, StringComparison.Ordinal);
        Assert.Contains("backgroundWindow:", body, StringComparison.Ordinal);
        Assert.Contains("backgroundReadLimit:", body, StringComparison.Ordinal);
        Assert.Contains("maxConcurrentBackups:", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "|| null", body, StringComparison.Ordinal,
            "an emptied field must clear, and null would keep the stored value instead");
    }

    [TestMethod]
    public void TheDestinationForm_CarriesTheLimitAndTheCadence()
    {
        var script = AppJs();
        Assert.Contains("id=\"dest-limit\"", script, StringComparison.Ordinal);
        Assert.Contains("id=\"dest-drill\"", script, StringComparison.Ordinal);

        var body = ActionBody("dest-save");
        Assert.Contains("transferLimit:", body, StringComparison.Ordinal);
        Assert.Contains("drillIntervalDays:", body, StringComparison.Ordinal);
    }
}
