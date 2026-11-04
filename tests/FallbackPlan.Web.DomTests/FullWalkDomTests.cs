using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console sets a set's full walk and shows the last one (FR-SNP-012,
/// ADR-0189 Amendment 1). The full walk is set beside the set's schedule, in
/// the same choices: daily by default, every so many minutes, hours or days,
/// or daily at a time. The set's summary says it, an edit always sends what
/// it shows (empty for the default, never the silence that keeps whatever
/// stands), and the new-set wizard's last step takes it with the schedule.
/// A set's card on the overview says when it was last walked in full, and
/// nothing for a set whose backups ask no change journal.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class FullWalkDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DestinationDescriptor Vault =>
        new("dest-1", "vault", "local-path", "/backups", null, null);

    [TestMethod]
    public async Task AnExistingSet_SaysWhenItIsWalkedInFull_AndItsScheduleSectionChangesIt()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set() with { Schedule = "every 1h", FullWalk = "daily at 02:00" }]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("walked in full daily at 02:00");

        await page.ClickAsync("[data-action=\"sec-schedule\"]");
        await Expect(page.Locator("input[name=fw-mode][value=daily]")).ToBeCheckedAsync();
        await Expect(page.Locator("#fw-time")).ToHaveValueAsync("02:00");
        await page.CheckAsync("input[name=fw-mode][value=every]");
        await page.FillAsync("#fw-n", "7");
        await page.SelectOptionAsync("#fw-unit", "d");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("walked in full every 7 days");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("every 7d", upsert.Set.FullWalk);
        Assert.AreEqual("every 1h", upsert.Set.Schedule, "the set's schedule is its own");
    }

    [TestMethod]
    public async Task AnExistingSet_PutBackToTheDefault_SendsTheEmptyThatClearsIt()
    {
        // Contract 1.82: null keeps what stands, so the default has to be
        // spoken as empty.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set() with { FullWalk = "every 7d" }]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);

        await page.ClickAsync("[data-action=\"sec-schedule\"]");
        await Expect(page.Locator("input[name=fw-mode][value=every]")).ToBeCheckedAsync();
        await Expect(page.Locator("#fw-n")).ToHaveValueAsync("7");
        await page.CheckAsync("input[name=fw-mode][value=default]");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("walked in full daily (the default)");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual(string.Empty, upsert.Set.FullWalk);
    }

    [TestMethod]
    public async Task AnEditThatLeavesTheFullWalkAlone_SendsWhatTheSetHas()
    {
        // The editor shows the full walk, so it says what it shows: a rename
        // carries the set's own, and a set naming none sends the default.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets:
        [
            Wire.Set() with { FullWalk = "every 6h" },
            Wire.Set("photos", new string('b', 32)),
        ]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);
        await page.ClickAsync("[data-action=\"sec-name\"]");
        await page.FillAsync("#set-name", "documents");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");
        Assert.AreEqual("every 6h", (await harness.ReceivedAsync<UpsertBackupSetCommand>()).Set.FullWalk);

        var photos = await OpenSummaryAsync(harness, context, "photos");
        await Expect(photos.Locator("#set-summary")).ToContainTextAsync("walked in full daily (the default)");
        await photos.ClickAsync("[data-action=\"sec-name\"]");
        await photos.FillAsync("#set-name", "pictures");
        await photos.ClickAsync("[data-action=\"sec-save\"]");
        await photos.ClickAsync("[data-action=\"set-confirm-all\"]");
        var renamed = await harness.ReceivedAsync<UpsertBackupSetCommand>(upsert => upsert.Set.Name == "pictures");
        Assert.AreEqual(string.Empty, renamed.Set.FullWalk);
    }

    [TestMethod]
    public async Task TheNewSetWizard_TakesTheFullWalkOnItsLastStep()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");
        var add = page.Locator("[data-action=\"cfg-add-set\"]");
        await Expect(add).ToBeEnabledAsync();
        await add.ClickAsync();

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        var step = page.Locator("#set-editor");
        await Expect(step).ToHaveAttributeAsync("data-section", "other");
        await Expect(page.Locator("input[name=fw-mode][value=default]")).ToBeCheckedAsync();
        await page.CheckAsync("input[name=fw-mode][value=daily]");
        await page.FillAsync("#fw-time", "01:30");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("daily at 01:30", upsert.Set.FullWalk);
    }

    [TestMethod]
    public async Task Overview_ASetsCard_SaysWhenItWasLastWalkedInFull_AndOneThatAsksNoJournalSaysNothing()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult(
                "vm",
                [
                    new BackupSetStatusDescriptor(
                        "docs", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations: [], LastFullWalkAt: now - (3 * 3_600_000)),
                    new BackupSetStatusDescriptor(
                        "photos", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations: []),
                ],
                now,
                []),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var docs = page.Locator("details.set[data-set=\"docs\"]");
        await docs.Locator("summary").ClickAsync();
        await Expect(docs).ToContainTextAsync("walked in full 3 h ago");

        var photos = page.Locator("details.set[data-set=\"photos\"]");
        await photos.Locator("summary").ClickAsync();
        await Expect(photos).Not.ToContainTextAsync("walked in full");
    }

    private static Func<ServiceCommand, ServiceResult> Service(IReadOnlyList<BackupSetDescriptor>? sets = null) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult(sets ?? []),
            BrowseFoldersCommand { Path: null } => new FolderListingResult(
                null, null, [new FolderDescriptor("data", "/data", false, false)], []),
            BrowseFoldersCommand => new FolderListingResult("/data", null, [], []),
            ValidateSetDraftCommand => new SetDraftValidationResult([], []),
            UpsertBackupSetCommand => new ConfigurationChangeResult(["The set is saved."]),
            _ => new AcknowledgedResult(),
        };

    private static async Task<IPage> OpenSummaryAsync(DomHarness harness, IBrowserContext context, string name = "docs")
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");
        await page.ClickAsync($"[data-action=\"cfg-edit-set\"][data-name=\"{name}\"]");
        await Expect(page.Locator("#set-summary")).ToBeVisibleAsync();
        return page;
    }
}
