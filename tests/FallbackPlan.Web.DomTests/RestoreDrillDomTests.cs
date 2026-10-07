using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A destination's restore drill, run from its row on the overview
/// (FR-DRL-003, contract 1.58): the button asks the service to drill that one
/// pair now, and the page shows the service's answer and reads the row again,
/// where the drill is recorded as a scheduled one is.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreDrillDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DestinationStatusDescriptor Destination(string name) =>
        new(name, "local-path", "in-sync", NowMs, null, "independent", "verified",
            FilesHeld: 400, FilesTotal: 400, HoldsNewest: true);

    [TestMethod]
    public async Task ADestinationsDrillButton_DrillsThatPair_AndShowsTheServicesAnswer()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult(
                "vm",
                [
                    new BackupSetStatusDescriptor(
                        "docs", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations: [Destination("vault"), Destination("offsite")]),
                ],
                NowMs,
                []),
            ListJobsCommand => new JobsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            RunDrillCommand => new DrillResult(
                ["docs -> offsite: restored 3 file(s) from its replica"], Failed: 0, NotDrilled: 0),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();

        // The second destination, so the pair is the row's own and not the
        // set's first.
        var offsite = page.Locator("details.dest[data-dest=\"docs|offsite\"]");
        await offsite.Locator("> summary").ClickAsync();
        await offsite.Locator("[data-action=\"drill\"]").ClickAsync();

        var asked = await harness.ReceivedAsync<RunDrillCommand>();
        Assert.AreEqual("docs", asked.BackupSetName);
        Assert.AreEqual("offsite", asked.DestinationName);
        await Expect(page.Locator("#dialog").GetByText("docs -> offsite: restored 3 file(s) from its replica"))
            .ToBeVisibleAsync();
    }
}
