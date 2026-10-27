using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The restores the service kept receipts for, in the console's Maintenance
/// view (FR-RST-004, ADR-0125), walked in a real browser: each run with its
/// outcome, when it ended, what became of its files and where it wrote them,
/// the newest under the limit the console asks with and how many are kept; a
/// receipt the service could not read said to be unreadable; and a service
/// before contract 1.73 an empty card, not an error.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreReceiptsDomTests
{
    [TestMethod]
    public async Task TheMaintenanceView_ListsEachRestore_WithWhatBecameOfItsFiles_AndWhereItWrote()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new RestoreReceiptsResult(
            [
                new RestoreReceiptDescriptor(
                    "0123456789abcdef", "partial", 1_700_000_000_000, new string('e', 32), "/srv/restored",
                    Restored: 3, FoundInPlace: 1, Skipped: 2, Failed: 1, Degraded: 1),
                new RestoreReceiptDescriptor(
                    "fedcba9876543210", null, null, null, null, 0, 0, 0, 0, 0, Problem: "not a receipt"),
            ],
            Total: 60),
            _ => new AcknowledgedResult(),
        };
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        var card = page.Locator("#restore-receipts-card");
        await Expect(card).ToContainTextAsync("0123456789abcdef");
        await Expect(card).ToContainTextAsync("partial");
        await Expect(card).ToContainTextAsync("3 restored (1 found in place), 2 skipped, 1 failed, 1 degraded");
        await Expect(card).ToContainTextAsync("/srv/restored");
        await Expect(card).ToContainTextAsync("fedcba9876543210");
        await Expect(card).ToContainTextAsync("unreadable");
        await Expect(card).ToContainTextAsync("not a receipt");
        await Expect(card).ToContainTextAsync("Showing the newest 2 of 60 kept.");
        var asked = await harness.ReceivedAsync<ListRestoreReceiptsCommand>();
        Assert.AreEqual(50, asked.Limit);
    }

    [TestMethod]
    public async Task EveryReceiptKept_Shown_SaysNothingOfTheRest()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new RestoreReceiptsResult(
            [
                new RestoreReceiptDescriptor(
                    "0123456789abcdef", "complete", 1_700_000_000_000, new string('e', 32), "/srv/restored",
                    Restored: 2, FoundInPlace: 0, Skipped: 0, Failed: 0, Degraded: 0),
            ],
            Total: 1),
            _ => new AcknowledgedResult(),
        };
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        var card = page.Locator("#restore-receipts-card");
        await Expect(card).ToContainTextAsync("2 restored, 0 skipped, 0 failed");
        await Expect(card).Not.ToContainTextAsync("found in place");
        await Expect(card).Not.ToContainTextAsync("degraded");
        await Expect(card).Not.ToContainTextAsync("Showing the newest");
    }

    [TestMethod]
    public async Task AServiceThatKeptNone_SaysSo()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new RestoreReceiptsResult([], Total: 0),
            _ => new AcknowledgedResult(),
        };
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await Expect(page.Locator("#restore-receipts-card")).ToContainTextAsync("No restore has kept a receipt here yet.");
    }

    [TestMethod]
    public async Task AServiceBeforeTheListing_IsAnEmptyCard_AndNoError()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new ServiceError(ServiceErrorReason.InvalidArgument, "unknown command"),
            _ => new AcknowledgedResult(),
        };
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await Expect(page.Locator("#restore-receipts-card")).ToContainTextAsync("No restore has kept a receipt here yet.");
        await harness.ReceivedAsync<ListRestoreReceiptsCommand>();
        await Expect(page.Locator("#toasts")).Not.ToContainTextAsync("unknown command");
    }
}
