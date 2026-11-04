using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console lets a set leave its online-only files to their cloud provider,
/// and says where one does (FR-SNP-015, ADR-0195 Amendment 1). The choice is
/// made among a set's other settings, unticked by default. The set's summary
/// says it where it is made. An edit always sends what it shows, never the
/// silence that keeps whatever stands, and the new-set wizard's last step
/// takes it. A set's card on the overview says it leaves them, and says
/// nothing for a set that fetches them.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class LeaveOnlineOnlyDomTests
{
    private static DestinationDescriptor Vault =>
        new("dest-1", "vault", "local-path", "/backups", null, null);

    [TestMethod]
    public async Task AnExistingSet_ItsOtherSettingsLeaveThem_AndTheSummarySaysSo()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set() with { LeaveOnlineOnly = false }]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);
        await Expect(page.Locator("#set-summary")).Not.ToContainTextAsync("left to their cloud provider");

        await page.ClickAsync("[data-action=\"sec-other\"]");
        await Expect(page.Locator("#set-leave-online-only")).Not.ToBeCheckedAsync();
        await page.CheckAsync("#set-leave-online-only");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("online-only files left to their cloud provider");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.IsTrue(upsert.Set.LeaveOnlineOnly);
    }

    [TestMethod]
    public async Task AnExistingSetThatLeavesThem_Unticked_SendsFalse()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set() with { LeaveOnlineOnly = true }]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("online-only files left to their cloud provider");

        await page.ClickAsync("[data-action=\"sec-other\"]");
        await Expect(page.Locator("#set-leave-online-only")).ToBeCheckedAsync();
        await page.UncheckAsync("#set-leave-online-only");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await Expect(page.Locator("#set-summary")).Not.ToContainTextAsync("left to their cloud provider");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.IsFalse(upsert.Set.LeaveOnlineOnly);
    }

    [TestMethod]
    public async Task AnEditThatLeavesTheChoiceAlone_SendsWhatTheSetHas()
    {
        // The editor shows the choice, so it says what it shows: a rename
        // carries the set's own, true or false, never the silence of null.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets:
        [
            Wire.Set() with { LeaveOnlineOnly = true },
            Wire.Set("photos", new string('b', 32)),
        ]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenSummaryAsync(harness, context);
        await page.ClickAsync("[data-action=\"sec-name\"]");
        await page.FillAsync("#set-name", "documents");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");
        Assert.IsTrue((await harness.ReceivedAsync<UpsertBackupSetCommand>()).Set.LeaveOnlineOnly);

        var photos = await OpenSummaryAsync(harness, context, "photos");
        await photos.ClickAsync("[data-action=\"sec-name\"]");
        await photos.FillAsync("#set-name", "pictures");
        await photos.ClickAsync("[data-action=\"sec-save\"]");
        await photos.ClickAsync("[data-action=\"set-confirm-all\"]");
        var renamed = await harness.ReceivedAsync<UpsertBackupSetCommand>(upsert => upsert.Set.Name == "pictures");
        Assert.IsFalse(renamed.Set.LeaveOnlineOnly, "a set that names none fetches them, and says so");
    }

    [TestMethod]
    public async Task TheNewSetWizard_TakesItOnItsLastStep()
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

        await Expect(page.Locator("#set-editor")).ToHaveAttributeAsync("data-section", "other");
        await Expect(page.Locator("#set-leave-online-only")).Not.ToBeCheckedAsync();
        await page.CheckAsync("#set-leave-online-only");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.IsTrue(upsert.Set.LeaveOnlineOnly);
    }

    [TestMethod]
    public async Task Overview_ASetsCard_SaysItLeavesThem_AndOneThatFetchesThemSaysNothing()
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
                        Destinations: [], LeavesOnlineOnly: true),
                    new BackupSetStatusDescriptor(
                        "photos", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations: []),
                ],
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                []),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var docs = page.Locator("details.set[data-set=\"docs\"]");
        await docs.Locator("summary").ClickAsync();
        await Expect(docs.Locator("[data-leaves-online-only]")).ToContainTextAsync(
            "Online-only files are left to their cloud provider: one new or changed there is not downloaded, and is kept only by its provider");

        var photos = page.Locator("details.set[data-set=\"photos\"]");
        await photos.Locator("summary").ClickAsync();
        await Expect(photos.Locator("[data-leaves-online-only]")).ToHaveCountAsync(0);
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
