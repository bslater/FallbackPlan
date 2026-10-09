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
/// The wizard keeps one size from its first step to its last, every control
/// in it draws its whole focus ring, and its folder tree shows a folder
/// nothing in which is captured a shade lighter rather than struck through,
/// with toggles large enough to hit. Its retention step, and an existing
/// set's, say how long a deleted file stays restorable (FR-GC-014).
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
        await page.FillAsync("#ret-deleted", "90");
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
        Assert.AreEqual(
            new RetentionPolicyDescriptor(KeepDaily: 7, MinGenerations: 3, KeepDeletedDays: 90), upsert.Set.Retention);
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
    public async Task EveryStep_KeepsOneSize_TheSourcesStepWithItsFolderTreeAtFullHeight()
    {
        // 780 by 692 is the size the sources step takes with its folder tree
        // at its full 300 px. Every step keeps it, so neither the dialog nor
        // its buttons move as the walk goes on.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        await page.SetViewportSizeAsync(1280, 1000);
        var step = page.Locator("#set-editor");
        var sizes = new List<(string Step, LocatorBoundingBoxResult Box)>();
        async Task MeasureAsync(string label) => sizes.Add((label, (await page.Locator("#dialog").BoundingBoxAsync())!));

        await Expect(step).ToHaveAttributeAsync("data-section", "name");
        await MeasureAsync("name");
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await Expect(step).ToHaveAttributeAsync("data-section", "sources");
        await Expect(page.Locator("input.mark[data-mark-path=\"/data\"]")).ToBeVisibleAsync();
        await MeasureAsync("sources, closed");
        await page.ClickAsync("[data-action=\"sel-open\"][data-path=\"/data\"]");
        await Expect(page.Locator("#sel-tree")).ToContainTextAsync("(empty)");
        await MeasureAsync("sources, a folder open");
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await MeasureAsync("destinations");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        foreach (var key in new[] { "exclusions", "retention", "other" })
        {
            await Expect(step).ToHaveAttributeAsync("data-section", key);
            await MeasureAsync(key);
            if (key != "other")
            {
                await page.ClickAsync("[data-action=\"wiz-next\"]");
            }
        }

        foreach (var (label, box) in sizes)
        {
            Assert.AreEqual(780, box.Width, 0.5, label);
            Assert.AreEqual(692, box.Height, 0.5, label);
        }
    }

    [TestMethod]
    public async Task OnAWindowTooShortForIt_TheWizardTakesFourFifthsOfIt_AndKeepsItsButtonsInView()
    {
        // Only the step's own content scrolls. A dialog that scrolled whole
        // would carry its buttons out of sight on a short window.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        await page.SetViewportSizeAsync(1280, 600);
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(page.Locator("#set-editor")).ToHaveAttributeAsync("data-section", "sources");
        await Expect(page.Locator("input.mark[data-mark-path=\"/data\"]")).ToBeVisibleAsync();

        var dialog = (await page.Locator("#dialog").BoundingBoxAsync())!;
        Assert.AreEqual(480, dialog.Height, 0.5, "four fifths of a 600 px window");
        var next = (await page.Locator("[data-action=\"wiz-next\"]").BoundingBoxAsync())!;
        Assert.IsTrue(
            next.Y + next.Height <= dialog.Y + dialog.Height && next.Y + next.Height <= 600,
            $"Next ends at {next.Y + next.Height}, below the dialog's {dialog.Y + dialog.Height}");
    }

    [TestMethod]
    [DataRow(1000, DisplayName = "a window the wizard fits")]
    [DataRow(600, DisplayName = "a window short enough that the steps scroll")]
    public async Task EveryControlTheKeyboardReaches_DrawsItsWholeFocusRing_OnEveryStep(int windowHeight)
    {
        // The step's content scrolls, so it clips. A focus ring is drawn
        // outside its control's box, and the name step's full-width field
        // lost the ring's sides and its rounded corners to the clip. On a
        // short window a control Tab scrolls into view stops at the edge of
        // the clip, and its ring there is the one at risk.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service();
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        await page.SetViewportSizeAsync(1280, windowHeight);
        var step = page.Locator("#set-editor");
        var reached = new List<string>();
        var clipped = new List<string>();

        // Tab is what draws a keyboard's focus ring on every kind of
        // control, and a dialog's Tab order cycles, so a walk stops once a
        // control of this step comes round again.
        async Task WalkAsync(string key)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var press = 0; press < 40; press++)
            {
                await page.Keyboard.PressAsync("Tab");
                var ring = await page.EvaluateAsync<string?>(RingSpareScript);
                if (ring is null)
                {
                    continue;
                }

                var (control, spare) = (ring[..ring.LastIndexOf('|')], double.Parse(
                    ring[(ring.LastIndexOf('|') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                if (!seen.Add(control))
                {
                    return;
                }

                reached.Add($"{key}: {control}");
                if (spare < -0.5)
                {
                    clipped.Add($"{key}: {control} loses {-spare:0.#} px of its ring");
                }
            }
        }

        await Expect(step).ToHaveAttributeAsync("data-section", "name");
        await WalkAsync("name");
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await Expect(step).ToHaveAttributeAsync("data-section", "sources");
        await Expect(page.Locator("input.mark[data-mark-path=\"/data\"]")).ToBeVisibleAsync();
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        await WalkAsync("sources");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await Expect(step).ToHaveAttributeAsync("data-section", "destinations");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await WalkAsync("destinations");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        foreach (var key in new[] { "exclusions", "retention", "other" })
        {
            await Expect(step).ToHaveAttributeAsync("data-section", key);
            await WalkAsync(key);
            if (key != "other")
            {
                await page.ClickAsync("[data-action=\"wiz-next\"]");
            }
        }

        Assert.Contains("name: set-name", reached, string.Join("\n", reached));
        Assert.IsEmpty(clipped, string.Join("\n", clipped));
    }

    [TestMethod]
    public async Task TheFolderTree_HasLargeToggles_AndShowsAFolderNothingInWhichIsCapturedLighter_NotStruckThrough()
    {
        // Strikethrough read as deleted. A folder nothing in which is captured
        // is now a shade lighter; one with anything captured in it is in the
        // text colour, a partly captured folder among them. The checkbox says
        // which way each row is, so the colour carries no meaning alone.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(browse: browse => browse.Path switch
        {
            null => new FolderListingResult(null, null, [new FolderDescriptor("data", "/data", false, false)], []),
            "/data" => new FolderListingResult(
                "/data", null,
                [new FolderDescriptor("docs", "/data/docs", false, false), new FolderDescriptor("media", "/data/media", false, false)],
                []),
            _ => new FolderListingResult(browse.Path, "/data", [], []),
        });
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenWizardAsync(harness, context);
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");
        await Expect(page.Locator("input.mark[data-mark-path=\"/data\"]")).ToBeVisibleAsync();
        var text = await ColourOfAsync(page, "--text");
        var muted = await ColourOfAsync(page, "--muted");

        var toggle = page.Locator("[data-action=\"sel-open\"][data-path=\"/data\"]");
        var box = (await toggle.BoundingBoxAsync())!;
        Assert.IsTrue(box.Width >= 24 && box.Height >= 24, $"the toggle is {box.Width} by {box.Height}");
        var icon = (await toggle.Locator("svg").BoundingBoxAsync())!;
        Assert.IsTrue(icon.Height >= 16, $"the toggle's icon is {icon.Height} px tall");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");

        Assert.AreEqual("none", await NameStyleAsync(page, "/data", "textDecorationLine"));
        Assert.AreEqual(muted, await NameStyleAsync(page, "/data", "color"), "nothing in it is captured");

        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await page.CheckAsync("input.mark[data-mark-path=\"/data/docs\"]");

        Assert.AreEqual(text, await NameStyleAsync(page, "/data/docs", "color"), "captured");
        Assert.AreEqual(text, await NameStyleAsync(page, "/data", "color"), "partly captured");
        Assert.AreEqual(muted, await NameStyleAsync(page, "/data/media", "color"), "left out");
        Assert.AreEqual("none", await NameStyleAsync(page, "/data/media", "textDecorationLine"));
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

    [TestMethod]
    public async Task AnExistingSet_SaysHowLongItKeepsADeletedFile_AndAnEmptiedFieldSendsTheZeroThatClearsIt()
    {
        // FR-GC-014, contract 1.62: an absent duration keeps what stands, so
        // a field the person emptied has to say zero, at the set and in a
        // destination's override alike.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(sets:
        [
            Wire.Set() with
            {
                Retention = new RetentionPolicyDescriptor(KeepDaily: 7, KeepDeletedDays: 90),
                DestinationRetention = new Dictionary<string, RetentionPolicyDescriptor>
                {
                    ["vault"] = new(KeepMonthly: 4, KeepDeletedDays: 30),
                },
            },
        ]);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-edit-set\"][data-name=\"docs\"]");
        await Expect(page.Locator("#set-summary")).ToContainTextAsync("a deleted file stays restorable for 90 days");

        await page.ClickAsync("[data-action=\"sec-retention\"]");
        await Expect(page.Locator("#ret-deleted")).ToHaveValueAsync("90");
        await page.FillAsync("#ret-deleted", "");
        await page.ClickAsync("[data-action=\"sec-save\"]");

        await page.ClickAsync("[data-action=\"sec-destinations\"]");
        var overridden = page.Locator("[data-ovr=\"vault:keepDeletedDays\"]");
        await Expect(overridden).ToHaveValueAsync("30");
        await overridden.FillAsync("");
        await page.ClickAsync("[data-action=\"sec-save\"]");
        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual(new RetentionPolicyDescriptor(KeepDaily: 7, KeepDeletedDays: 0), upsert.Set.Retention);
        Assert.AreEqual(
            new RetentionPolicyDescriptor(KeepMonthly: 4, KeepDeletedDays: 0), upsert.Set.DestinationRetention!["vault"]);
    }

    private static Func<ServiceCommand, ServiceResult> Service(
        IReadOnlyList<BackupSetDescriptor>? sets = null,
        IReadOnlyList<DestinationDescriptor>? destinations = null,
        Func<ValidateSetDraftCommand, SetDraftValidationResult>? validate = null,
        Func<BrowseFoldersCommand, FolderListingResult>? browse = null) => command => command switch
    {
        DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
        ListDestinationsCommand => new DestinationsResult(destinations ?? [Vault]),
        ListBackupSetsCommand => new BackupSetsResult(sets ?? []),
        BrowseFoldersCommand folders when browse is not null => browse(folders),
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

    /// <summary>
    /// For the focused control, when it is in the wizard's step content: its
    /// name, a bar, and the room its focus ring has inside every ancestor that
    /// clips it, up to the step content, in pixels. Negative is how much of
    /// the ring is cut away. Null when focus is anywhere else. A ring the
    /// browser draws itself (<c>auto</c>) is taken as two pixels wide.
    /// </summary>
    private const string RingSpareScript =
        """
        () => {
          const control = document.activeElement;
          const body = document.querySelector("#set-editor .wiz-body");
          if (!control || !body || !body.contains(control)) return null;
          const style = getComputedStyle(control);
          const width = style.outlineStyle === "none" ? 0
            : style.outlineStyle === "auto" ? Math.max(2, parseFloat(style.outlineWidth) || 0)
            : parseFloat(style.outlineWidth) || 0;
          const reach = width === 0 ? 0 : width + (parseFloat(style.outlineOffset) || 0);
          const box = control.getBoundingClientRect();
          let spare = Number.MAX_VALUE;
          for (let clip = control.parentElement; clip; clip = clip.parentElement) {
            const clipStyle = getComputedStyle(clip);
            if (clipStyle.overflowX !== "visible" || clipStyle.overflowY !== "visible") {
              const outer = clip.getBoundingClientRect();
              const left = outer.left + clip.clientLeft;
              const top = outer.top + clip.clientTop;
              spare = Math.min(
                spare,
                box.left - reach - left,
                left + clip.clientWidth - (box.right + reach),
                box.top - reach - top,
                top + clip.clientHeight - (box.bottom + reach));
            }
            if (clip === body) break;
          }
          const name = control.id || control.dataset.action || control.dataset.destCheck
            || control.dataset.markPath || control.name || control.outerHTML.slice(0, 60);
          return `${name}|${spare}`;
        }
        """;

    /// <summary>What a colour token computes to on this page, in the form a computed colour takes.</summary>
    private static Task<string> ColourOfAsync(IPage page, string token) => page.EvaluateAsync<string>(
        """
        token => {
          const probe = document.createElement("span");
          probe.style.color = `var(${token})`;
          document.body.append(probe);
          const colour = getComputedStyle(probe).color;
          probe.remove();
          return colour;
        }
        """,
        token);

    /// <summary>A computed style of the name in the folder tree's row for <paramref name="path"/>.</summary>
    private static Task<string> NameStyleAsync(IPage page, string path, string property) =>
        page.Locator($"#sel-tree .tree-row:has(input[data-mark-path=\"{path}\"]) .tree-name")
            .EvaluateAsync<string>("(name, property) => getComputedStyle(name)[property]", property);

    private static int UpsertsSent(DomHarness harness)
    {
        lock (harness.Clients.Client.Received)
        {
            return harness.Clients.Client.Received.OfType<UpsertBackupSetCommand>().Count();
        }
    }
}
