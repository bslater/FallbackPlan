using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// What a cloud destination's store was asked, on its card on the overview
/// (FR-DEST-019, contract 1.69): the requests sent over the window a bill is
/// read in, by operation, and the bytes they carried both ways. A destination
/// the service counts nothing for draws no row, never a row of zeros, and a
/// count that began inside the window says when.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class DestinationRequestsDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task ACloudDestinationsCard_SaysWhatItsStoreWasAsked_AndALocalPathsSaysNothing()
    {
        await using var harness = await StartAsync(new StoreRequestsDescriptor(
            Days: 30, Puts: 12, Gets: 40, Heads: 3, Lists: 5, Deletes: 1, BytesSent: 4_096, BytesReceived: 300));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenAsync(context, harness);

        var cloud = page.Locator("details.dest[data-dest=\"docs|cloud\"]");
        await cloud.Locator("> summary").ClickAsync();
        await Expect(cloud.GetByText("12 put · 40 get · 3 head · 5 list · 1 delete")).ToBeVisibleAsync();
        await Expect(cloud.GetByText("in the last 30 days · 4.0 KiB sent, 300 B received")).ToBeVisibleAsync();
        await Expect(cloud.GetByText("counted since")).ToHaveCountAsync(0);

        var vault = page.Locator("details.dest[data-dest=\"docs|vault\"]");
        await vault.Locator("> summary").ClickAsync();
        await Expect(vault.GetByText("Failure domain")).ToBeVisibleAsync();
        await Expect(vault.GetByText("Requests", new() { Exact = true })).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task ACountBegunInsideTheWindow_SaysWhenItBegan()
    {
        await using var harness = await StartAsync(new StoreRequestsDescriptor(
            Days: 30, Puts: 1, Gets: 0, Heads: 0, Lists: 0, Deletes: 0, BytesSent: 10, BytesReceived: 0,
            Since: NowMs - 86_400_000));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenAsync(context, harness);

        var cloud = page.Locator("details.dest[data-dest=\"docs|cloud\"]");
        await cloud.Locator("> summary").ClickAsync();
        await Expect(cloud.GetByText("counted since", new() { Exact = false })).ToBeVisibleAsync();
    }

    private static async Task<DomHarness> StartAsync(StoreRequestsDescriptor requests)
    {
        var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult(
                "vm",
                [
                    new BackupSetStatusDescriptor(
                        "docs", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations:
                        [
                            new DestinationStatusDescriptor(
                                "vault", "local-path", "in-sync", NowMs, null, "independent", "verified",
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true),
                            new DestinationStatusDescriptor(
                                "cloud", "s3", "in-sync", NowMs, null, "offsite", "verified",
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true, Requests: requests),
                        ]),
                ],
                NowMs,
                []),
            ListJobsCommand => new JobsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };

        return harness;
    }

    private static async Task<Microsoft.Playwright.IPage> OpenAsync(
        Microsoft.Playwright.IBrowserContext context, DomHarness harness)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();
        return page;
    }
}
