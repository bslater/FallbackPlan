using System.Text.RegularExpressions;
using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The page chrome: the theme toggle's persistence and the staleness
/// presentation — the honesty rule that a page showing data it cannot
/// refresh must say so, and stop saying so the moment contact returns.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class ChromeDomTests
{
    [TestMethod]
    public async Task ThemeToggle_FlipsTheDocumentTheme_AndSurvivesAReload()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        // Wait for boot to have run — the toggle's listener is attached from
        // script, and a click that lands before it would silently do nothing.
        await Expect(page.Locator("#conn-text")).ToHaveTextAsync("service reachable");

        // System theme by default; the first click pins an explicit choice.
        await page.ClickAsync("#theme-toggle");
        var first = await page.EvaluateAsync<string>("document.documentElement.dataset.theme");
        CollectionAssert.Contains(new[] { "light", "dark" }, first);
        Assert.AreEqual(first, await page.EvaluateAsync<string>("localStorage.getItem('fbp-theme')"));

        await page.ClickAsync("#theme-toggle");
        var second = await page.EvaluateAsync<string>("document.documentElement.dataset.theme");
        Assert.AreEqual(first == "dark" ? "light" : "dark", second);

        // The pinned choice is applied at boot, before anything renders.
        await page.ReloadAsync();
        Assert.AreEqual(second, await page.EvaluateAsync<string>("document.documentElement.dataset.theme"));
    }

    [TestMethod]
    public async Task StaleBanner_AppearsWhileTheServiceIsGone_AndClearsOnRecontact()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#conn-text")).ToHaveTextAsync("service reachable");

        // Every connect now fails the way an absent service does; the next
        // poll turns the page honest about what it is showing.
        harness.Clients.Unreachable = true;
        await Expect(page.Locator("#stale-banner")).ToBeVisibleAsync();
        await Expect(page.Locator("#conn-text")).ToHaveTextAsync("service unreachable");
        await Expect(page.Locator("body")).ToHaveClassAsync(new Regex("\\bstale\\b"));
        await Expect(page.Locator("#stale-banner")).ToContainTextAsync("as of last contact");

        // Contact returns; the banner must clear on its own — a page that
        // cries stale forever is as dishonest as one that never does.
        harness.Clients.Unreachable = false;
        await Expect(page.Locator("#stale-banner")).ToBeHiddenAsync();
        await Expect(page.Locator("#conn-text")).ToHaveTextAsync("service reachable");
    }
}
