using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A new backup set is made in steps (FR-SVC-022): its name, what it backs
/// up with the selection filters that narrow it, and where it goes, each
/// answered before the next; then its exclusions, its retention and its
/// other settings, each of which may be left as it is. Walked by real clicks
/// in a real browser. Nothing reaches the service until Create, and Create
/// sends the one upsert carrying every step. The destinations step says the
/// refusal a local destination on a root's drive would meet before anything
/// is saved (FR-DEST-017), judged as the very set the save would create.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class NewSetWizardDomTests
{
    private const string PlacementRefusal =
        "Destination 'vault' shares a volume with root '/data' — a backup on the drive the files live on dies "
        + "with them. Choose a local destination on a different drive (ADR-0051).";

    private static DestinationDescriptor Vault =>
        new("dest-1", "vault", "local-path", "/backups", null, null);

    private static DestinationDescriptor Spare =>
        new("dest-2", "spare", "local-path", "/media/spare", null, null);

    [TestMethod]
    public async Task SixSteps_CreateTheSet_WithOneUpsertCarryingEveryStep()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        var step = page.Locator("#set-editor");

        // 1. The name.
        await Expect(step).ToHaveAttributeAsync("data-section", "name");
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // 2. What is backed up, and the selection filter that narrows it.
        await Expect(step).ToHaveAttributeAsync("data-section", "sources");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.FillAsync("#rule-new", "*.docx");
        await page.ClickAsync("[data-action=\"rule-add-raw\"]");
        await Expect(page.Locator("#rule-chips")).ToContainTextAsync("*.docx");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // 3. Where it goes.
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // 4. What it leaves out.
        await Expect(step).ToHaveAttributeAsync("data-section", "exclusions");
        await page.FillAsync("#rule-new", "*.iso");
        await page.ClickAsync("[data-action=\"rule-add-raw\"]");
        await Expect(page.Locator("#rule-chips")).ToContainTextAsync("*.iso");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // 5. How long it keeps.
        await Expect(step).ToHaveAttributeAsync("data-section", "retention");
        await page.FillAsync("#ret-daily", "7");
        await page.FillAsync("#ret-min", "3");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // 6. The rest: when it runs, how it ranks, and its storage shape.
        await Expect(step).ToHaveAttributeAsync("data-section", "other");
        await Expect(page.Locator("[data-action=\"wiz-next\"]")).ToHaveCountAsync(0);
        await page.CheckAsync("input[name=sched-mode][value=daily]");
        await page.FillAsync("#sched-time", "03:15");
        await page.FillAsync("#set-priority", "5");
        await page.UncheckAsync("#set-direct-ship");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("docs", upsert.Set.Name);
        Assert.AreEqual("/data", Assert.ContainsSingle(upsert.Set.Roots!).Path);
        CollectionAssert.AreEqual(new[] { "*.docx" }, upsert.Set.IncludeRules.ToList());
        CollectionAssert.AreEqual(new[] { "*.iso" }, upsert.Set.ExcludeRules.ToList());
        CollectionAssert.AreEqual(new[] { "vault" }, upsert.Set.Destinations.ToList());
        Assert.AreEqual(new RetentionPolicyDescriptor(KeepDaily: 7, MinGenerations: 3), upsert.Set.Retention);
        Assert.AreEqual("daily at 03:15", upsert.Set.Schedule);
        Assert.AreEqual(5, upsert.Set.Priority);
        Assert.IsFalse(upsert.Set.DirectShip);

        await Expect(page.Locator("#dialog").GetByText("Backup set 'docs' saved")).ToBeVisibleAsync();
        Assert.AreEqual(1, UpsertsSent(harness), "the six steps send one upsert, at Create");
    }

    [TestMethod]
    public async Task TheRequiredSteps_EachHoldUntilAnswered_AndNothingIsCreatedBeforeThem()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set("taken", new string('c', 32))]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        var step = page.Locator("#set-editor");
        var toasts = page.Locator("#toasts");
        var create = page.Locator("[data-action=\"wiz-create\"]");

        // The name: required, and not one another set already has.
        await Expect(create).ToHaveCountAsync(0);
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(toasts).ToContainTextAsync("A set needs a name.");
        await Expect(step).ToHaveAttributeAsync("data-section", "name");

        await page.FillAsync("#set-name", "taken");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(toasts).ToContainTextAsync("A set named 'taken' already exists.");
        await Expect(step).ToHaveAttributeAsync("data-section", "name");

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // What is backed up: at least one folder.
        await Expect(step).ToHaveAttributeAsync("data-section", "sources");
        await Expect(create).ToHaveCountAsync(0);
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(toasts).ToContainTextAsync("Tick at least one folder first.");
        await Expect(step).ToHaveAttributeAsync("data-section", "sources");

        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // Where it goes: at least one destination, for Next and Create alike.
        // Each refusal is its own toast, which is how the test knows Create
        // was heard and refused rather than still on its way.
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(toasts).ToContainTextAsync("Choose at least one destination.");
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await create.ClickAsync();
        await Expect(toasts.GetByText("Choose at least one destination.")).ToHaveCountAsync(2);
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");

        Assert.AreEqual(0, UpsertsSent(harness), "nothing is created before its required steps are answered");
    }

    [TestMethod]
    public async Task TheOptionalSteps_LeftAsTheyAre_CreateFromTheDestinationsStep()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("[data-dest-check=\"vault\"]");

        // Before Create, the step says what the steps not taken leave.
        var rest = page.Locator("#wiz-rest");
        await Expect(rest).ToContainTextAsync("keeps everything");
        await Expect(rest).ToContainTextAsync("manual");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("docs", upsert.Set.Name);
        Assert.IsEmpty(upsert.Set.IncludeRules);
        Assert.IsEmpty(upsert.Set.ExcludeRules);
        Assert.IsNull(upsert.Set.Retention, "no rule was set, so every backup is kept");
        Assert.IsNull(upsert.Set.Schedule, "a set no step scheduled runs when it is started");
        Assert.IsNull(upsert.Set.Priority);
        Assert.IsTrue(upsert.Set.DirectShip, "a new set with a local destination ships straight to it");
    }

    [TestMethod]
    public async Task Back_AndTheStepsPassed_KeepWhatEachStepHeld()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        var step = page.Locator("#set-editor");

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // A value typed and left by Back, not by Next, is kept too.
        await Expect(step).ToHaveAttributeAsync("data-section", "retention");
        await page.FillAsync("#ret-daily", "7");
        await page.ClickAsync("[data-action=\"wiz-back\"]");
        await page.ClickAsync("[data-action=\"wiz-back\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await Expect(page.Locator("[data-dest-check=\"vault\"]")).ToBeCheckedAsync();

        await page.ClickAsync("[data-action=\"wiz-back\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "sources");
        await Expect(page.Locator("input.mark[data-mark-path=\"/data\"]")).ToBeCheckedAsync();

        await page.ClickAsync("[data-action=\"wiz-back\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "name");
        await Expect(page.Locator("#set-name")).ToHaveValueAsync("docs");
        await Expect(page.Locator("[data-action=\"wiz-back\"]")).ToHaveCountAsync(0);

        // A step already reached is one click away on the step indicator.
        await page.ClickAsync("[data-action=\"wiz-goto\"][data-step=\"4\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "retention");
        await Expect(page.Locator("#ret-daily")).ToHaveValueAsync("7");
        await Expect(page.Locator("[data-action=\"wiz-goto\"][data-step=\"5\"]")).ToHaveCountAsync(0);
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("docs", upsert.Set.Name);
        Assert.AreEqual("/data", Assert.ContainsSingle(upsert.Set.Roots!).Path);
        CollectionAssert.AreEqual(new[] { "vault" }, upsert.Set.Destinations.ToList());
        Assert.AreEqual(7, upsert.Set.Retention?.KeepDaily);
    }

    [TestMethod]
    public async Task Create_AfterARequiredAnswerWasUndone_ReturnsToItsStep()
    {
        // The step indicator keeps a half-made answer, so Create asks again
        // whether every required step is answered rather than trusting the
        // walk that reached it.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        var step = page.Locator("#set-editor");

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("[data-dest-check=\"vault\"]");

        await page.ClickAsync("[data-action=\"wiz-goto\"][data-step=\"0\"]");
        await page.FillAsync("#set-name", "");
        await page.ClickAsync("[data-action=\"wiz-goto\"][data-step=\"2\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        await Expect(page.Locator("#toasts")).ToContainTextAsync("Step 1, Name, still needs an answer.");
        await Expect(step).ToHaveAttributeAsync("data-section", "name");
        Assert.AreEqual(0, UpsertsSent(harness));
    }

    [TestMethod]
    public async Task TheDestinationsStep_SaysAPlacementRefusal_AndHoldsUntilItIsResolved()
    {
        // FR-DEST-017: the refusal is said where the destination is chosen,
        // judged as the set the save will create, not after the optional
        // steps when Create meets it.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            destinations: [Vault, Spare],
            validate: draft => new SetDraftValidationResult(
                draft.Destinations?.Contains("vault") == true ? [PlacementRefusal] : [], []));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        var step = page.Locator("#set-editor");

        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await Expect(page.Locator("#draft-defects")).ToContainTextAsync("'vault' shares a volume with root '/data'");
        var judged = await harness.ReceivedAsync<ValidateSetDraftCommand>(draft =>
            draft.Destinations?.Contains("vault") == true);
        Assert.IsNotNull(judged.SetId, "a draft names the set it would create, or placement goes unjudged");

        var toasts = page.Locator("#toasts");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(toasts).ToContainTextAsync("Resolve what this step says first.");
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await page.ClickAsync("[data-action=\"wiz-create\"]");
        await Expect(toasts.GetByText("Resolve what this step says first.")).ToHaveCountAsync(2);
        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        Assert.AreEqual(0, UpsertsSent(harness), "a refusal the step has said is not sent to be said again");

        // Another drive resolves it.
        await page.UncheckAsync("[data-dest-check=\"vault\"]");
        await page.CheckAsync("[data-dest-check=\"spare\"]");
        await Expect(page.Locator("#draft-defects")).Not.ToContainTextAsync("shares a volume");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(step).ToHaveAttributeAsync("data-section", "exclusions");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual(judged.SetId, upsert.Set.Id, "the draft was judged as the set the save creates");
        CollectionAssert.AreEqual(new[] { "spare" }, upsert.Set.Destinations.ToList());
    }

    [TestMethod]
    public async Task AnExistingSet_StillOpensOnItsSummary()
    {
        // The steps are for making a set. Changing one is a single setting
        // at a time, so editing keeps the summary with a dialog per section.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets: [Wire.Set()]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-edit-set\"][data-name=\"docs\"]");
        await Expect(page.Locator("#set-summary")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-action=\"wiz-next\"]")).ToHaveCountAsync(0);

        await page.ClickAsync("[data-action=\"sec-name\"]");
        await page.FillAsync("#set-name", "documents");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual(Wire.SetId, upsert.Set.Id);
        Assert.AreEqual("documents", upsert.Set.Name);
    }

    private static Func<ServiceCommand, ServiceResult> Service(
        IReadOnlyList<BackupSetDescriptor>? sets = null,
        IReadOnlyList<DestinationDescriptor>? destinations = null,
        Func<ValidateSetDraftCommand, SetDraftValidationResult>? validate = null) => command => command switch
    {
        DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
        ListDestinationsCommand => new DestinationsResult(destinations ?? [Vault]),
        ListBackupSetsCommand => new BackupSetsResult(sets ?? []),
        BrowseFoldersCommand { Path: null } => new FolderListingResult(
            null, null, [new FolderDescriptor("data", "/data", false, false)], []),
        BrowseFoldersCommand => new FolderListingResult("/data", null, [], []),
        ValidateSetDraftCommand draft => validate?.Invoke(draft) ?? new SetDraftValidationResult([], []),
        UpsertBackupSetCommand => new ConfigurationChangeResult(["The set is saved."]),
        _ => new AcknowledgedResult(),
    };

    private static async Task<IPage> OpenWizardAsync(DomHarness harness, IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        // The add button is gated on a declared destination (FR-DEST-001).
        var add = page.Locator("[data-action=\"cfg-add-set\"]");
        await Expect(add).ToBeEnabledAsync();
        await add.ClickAsync();
        return page;
    }

    private static int UpsertsSent(DomHarness harness)
    {
        lock (harness.Clients.Client.Received)
        {
            return harness.Clients.Client.Received.OfType<UpsertBackupSetCommand>().Count();
        }
    }
}
