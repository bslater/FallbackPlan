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
/// before contract 1.73 an empty card, not an error. Each names the set it
/// restored where its receipt does (ADR-0126). What a run did not restore is
/// shown only once the passphrase of the set its receipt records has
/// unlocked a source, which is closed after (ADR-0127), and a run that
/// restored everything offers nothing to show. A run says how many of its
/// files were resumed within (ADR-0134). A receipt is let go by its run once
/// a person confirms, asking no passphrase, and the list is asked again
/// (ADR-0140).
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
                    Restored: 3, FoundInPlace: 1, Skipped: 2, Failed: 1, Degraded: 1, Set: "photos", ResumedWithin: 1),
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
        await Expect(card.Locator("tr", new() { HasText = "0123456789abcdef" })).ToContainTextAsync("photos");
        await Expect(card).ToContainTextAsync("partial");
        await Expect(card).ToContainTextAsync("3 restored (1 found in place, 1 resumed within), 2 skipped, 1 failed, 1 degraded");
        await Expect(card).ToContainTextAsync("/srv/restored");
        await Expect(card).ToContainTextAsync("fedcba9876543210");
        await Expect(card).ToContainTextAsync("unreadable");
        await Expect(card).ToContainTextAsync("not a receipt");
        await Expect(card).ToContainTextAsync("Showing the newest 2 of 60 kept.");
        var asked = await harness.ReceivedAsync<ListRestoreReceiptsCommand>();
        Assert.AreEqual(50, asked.Limit);
    }

    [TestMethod]
    public async Task WhatARunDidNotRestore_IsShownOnlyThroughThePassphrase_AndTheSourceIsClosedAfter()
    {
        await using var harness = await DomHarness.StartAsync();
        var receipt = new RestoreReceiptDescriptor(
            "0123456789abcdef", "partial", 1_700_000_000_000, new string('e', 32), "/srv/restored",
            Restored: 3, FoundInPlace: 0, Skipped: 1, Failed: 1, Degraded: 0, Set: "docs", SetId: Wire.SetId);
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new RestoreReceiptsResult(
            [
                receipt,
                new RestoreReceiptDescriptor(
                    "fedcba9876543210", "complete", 1_600_000_000_000, new string('e', 32), "/srv/whole",
                    Restored: 2, FoundInPlace: 0, Skipped: 0, Failed: 0, Degraded: 0, Set: "docs", SetId: Wire.SetId),
            ],
            Total: 2),
            OpenRestoreSourceCommand => new RestoreSourceOpenedResult("src-1", "docs", "staging", [], []),
            GetRestoreReceiptCommand { Source: "src-1" } => new RestoreReceiptResult(
                receipt,
                [
                    new RestoreReceiptItemDescriptor("kept.txt", "skipped", "a newer file was in the way"),
                    new RestoreReceiptItemDescriptor("lost.txt", "failed", "no copy would read"),
                ],
                Unlisted: 4),
            GetRestoreReceiptCommand => new ServiceError(ServiceErrorReason.Refused, "A receipt's files need the set's passphrase."),
            _ => new AcknowledgedResult(),
        };
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await Expect(page.Locator("[data-action=\"restore-receipt-files\"][data-run=\"fedcba9876543210\"]"))
            .ToHaveCountAsync(0);
        await page.ClickAsync("[data-action=\"restore-receipt-files\"][data-run=\"0123456789abcdef\"]");
        await Expect(page.Locator("#gate-passphrase")).ToBeVisibleAsync();
        await page.FillAsync("#gate-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"gate-unlock\"]");

        var dialog = page.Locator("#dialog");
        await Expect(dialog).ToContainTextAsync("failed lost.txt — no copy would read");
        await Expect(dialog).ToContainTextAsync("skipped kept.txt — a newer file was in the way");
        await Expect(dialog).ToContainTextAsync("and 4 more");
        var asked = await harness.ReceivedAsync<GetRestoreReceiptCommand>();
        Assert.AreEqual("0123456789abcdef", asked.Run);
        Assert.AreEqual("src-1", asked.Source);
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<CloseRestoreSourceCommand>()).SourceId);
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
    public async Task AReceipt_IsLetGoByItsRun_AfterAPersonConfirms_AndTheListIsAskedAgain()
    {
        await using var harness = await DomHarness.StartAsync();
        var forgot = false;
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListRestoreReceiptsCommand => new RestoreReceiptsResult(
                forgot
                    ? []
                    : [
                        new RestoreReceiptDescriptor(
                            "0123456789abcdef", "complete", 1_700_000_000_000, new string('e', 32), "/srv/restored",
                            Restored: 3, FoundInPlace: 0, Skipped: 0, Failed: 0, Degraded: 0),
                    ],
                Total: forgot ? 0 : 1),
            ForgetRestoreReceiptCommand => Forgot(),
            _ => new AcknowledgedResult(),
        };
        ServiceResult Forgot()
        {
            forgot = true;
            return new AcknowledgedResult();
        }

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await page.ClickAsync("[data-action=\"forget-receipt\"][data-run=\"0123456789abcdef\"]");
        await Expect(page.Locator("#dialog")).ToContainTextAsync("0123456789abcdef");
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ForgetRestoreReceiptCommand>(), "nothing is let go before the person confirms");
        await page.ClickAsync("#forget-receipt-go");

        Assert.AreEqual("0123456789abcdef", (await harness.ReceivedAsync<ForgetRestoreReceiptCommand>()).Run);
        await Expect(page.Locator("#restore-receipts-card")).ToContainTextAsync("No restore has kept a receipt here yet.");
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<OpenRestoreSourceCommand>(), "letting a receipt go asks no passphrase");
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
