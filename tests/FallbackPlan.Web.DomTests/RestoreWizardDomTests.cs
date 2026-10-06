using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The guided restore wizard (ADR-0041) walked end to end in a real browser —
/// the committed counterpart of the "live Playwright walk" ADR-0041 cites.
/// The passphrase gate is the real thing (FR-WOR-007, ADR-0089): the console
/// process derives with Argon2id under the facts the service publishes, the
/// source opens under the grant that derivation proves, the secret is never
/// on the service wire (NFR-SEC-009), and there is no way through without a
/// passphrase that checks out. The last step confirms a plan, so nothing
/// there can be confirmed until the plan is on screen — the console's half of
/// FR-RST-003, which puts the plan before any byte is written. The plan is
/// asked with the run's shape, so it shows the room the run needs where it
/// will write, and a plan that will not fit arms the restore only when the
/// person chooses to restore anyway (ADR-0083). The result says which
/// captured metadata the files came back without (FR-RST-004). Its file tree
/// shows what will not be restored a shade lighter rather than struck
/// through, with toggles large enough to hit, as the backup wizard's does.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreWizardDomTests
{
    private const string RightPassphrase = Wire.Passphrase;

    private static Func<ServiceCommand, ServiceResult> WizardFakes(ulong now) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListSnapshotsCommand => new SnapshotsResult([Wire.Snapshot(now)]),
            OpenRestoreSourceCommand => new RestoreSourceOpenedResult(
                "src-1", "docs", "staging", [Wire.Snapshot(now)], []),
            ListDirectoryCommand => new DirectoryResult(
                "",
                [
                    new DirectoryEntryDescriptor("notes.txt", "file", 42, now, "same"),
                    new DirectoryEntryDescriptor("photos", "directory", 0),
                ]),
            PlanRestoreCommand => new RestorePlanResult(1, 42, []),
            RunRestoreCommand => new ApiRestoreResult(
                1, 0, "/restore/out", "complete", ReceiptPath: "/restore/out/receipt.json"),
            _ => new AcknowledgedResult(),
        };

    /// <summary>
    /// Steps 1 to 5 with every default taken, ending on the click that asks
    /// for the plan — the walk the end-to-end case narrates, for the cases
    /// that are about what step 6 does with it.
    /// </summary>
    private static async Task WalkToThePlanAsync(IPage page, DomHarness harness)
    {
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", RightPassphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-date")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await page.CheckAsync("input[data-rst-mark=\"notes.txt\"]");
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await page.FillAsync("#rst-output", "/restore/out");
        await page.ClickAsync("[data-action=\"rst-continue\"]");
    }

    [TestMethod]
    public async Task Wizard_WalkedEndToEnd_UnlocksPlansAndRestores()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = WizardFakes(now);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        // Step 1 — unlock: the typed passphrase is checked by the console
        // process against the sealing key the service publishes for the set.
        // A real derivation runs here.
        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", RightPassphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // Step 2 — source: the staging archive is the default choice.
        await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        await Expect(page.Locator("input[name=rst-src][value=\"staging\"]")).ToBeCheckedAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        var opened = await harness.ReceivedAsync<OpenRestoreSourceCommand>();
        Assert.AreEqual("docs", opened.SetName);
        Assert.IsNull(opened.DestinationName, "the staging archive is not a destination");
        Assert.IsTrue(Wire.IsTheInstallationsGrant(opened.Envelope), "the source opens under the set's grant");

        // Step 3 — date: today's default resolves to the only snapshot.
        await Expect(page.Locator("#rst-date")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // Step 4 — files: tick one file out of the listed snapshot root.
        await page.CheckAsync("input[data-rst-mark=\"notes.txt\"]");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // Step 5 — target: a chosen folder, keeping existing files (default).
        await page.FillAsync("#rst-output", "/restore/out");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // Step 6 — the plan arrives, and the typed word arms the button.
        var planned = await harness.ReceivedAsync<PlanRestoreCommand>(plan => plan.Source == "src-1");
        Assert.AreEqual("snap-1", planned.SnapshotId);
        var run = page.Locator("#rst-run-go");
        await Expect(run).ToBeDisabledAsync();
        await page.FillAsync("#confirm-word", "restore");
        await Expect(run).ToBeEnabledAsync();
        await run.ClickAsync();

        var restored = await harness.ReceivedAsync<RunRestoreCommand>();
        Assert.AreEqual("snap-1", restored.SnapshotId);
        Assert.AreEqual("/restore/out", restored.OutputDirectory);
        Assert.AreEqual("src-1", restored.Source);
        Assert.AreEqual("folder", restored.Target);
        Assert.AreEqual("rename", restored.Existing);
        Assert.IsTrue(restored.InPlace);
        Assert.IsNotNull(restored.Paths);
        CollectionAssert.Contains(restored.Paths.ToList(), "notes.txt");

        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();

        // Closing the wizard releases the server-side source handle.
        await page.ClickAsync("#dialog [data-action=\"close-dialog\"]");
        var closed = await harness.ReceivedAsync<CloseRestoreSourceCommand>();
        Assert.AreEqual("src-1", closed.SourceId);
    }

    [TestMethod]
    public async Task Wizard_TheFileTree_HasLargeToggles_AndShowsWhatIsNotChosenLighter_NotStruckThrough()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = WizardFakes(now);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", RightPassphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-date")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("input[data-rst-mark=\"notes.txt\"]")).ToBeVisibleAsync();
        var text = await page.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('dialog')).color");

        var toggle = page.Locator("[data-action=\"rst-open\"][data-path=\"photos\"]");
        var box = (await toggle.BoundingBoxAsync())!;
        Assert.IsTrue(box.Width >= 24 && box.Height >= 24, $"the toggle is {box.Width} by {box.Height}");
        Assert.IsTrue((await toggle.Locator("svg").BoundingBoxAsync())!.Height >= 16, "the toggle's icon is too small");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");

        Assert.AreEqual("none", await EntryStyleAsync(page, "notes.txt", "textDecorationLine"));
        Assert.AreNotEqual(text, await EntryStyleAsync(page, "notes.txt", "color"), "not chosen is a shade lighter");

        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await page.CheckAsync("input[data-rst-mark=\"photos/notes.txt\"]");
        await page.CheckAsync("input[data-rst-mark=\"notes.txt\"]");

        Assert.AreEqual(text, await EntryStyleAsync(page, "notes.txt", "color"), "chosen");
        Assert.AreEqual(text, await EntryStyleAsync(page, "photos", "color"), "partly chosen");
        Assert.AreNotEqual(text, await EntryStyleAsync(page, "photos/photos", "color"), "not chosen");
    }

    [TestMethod]
    public async Task Wizard_ARestoreThatReadAroundDamage_SaysSoOnItsResult()
    {
        // FR-RST-007 where the person restoring is looking: which files came
        // from another copy, and what the copy they were read around held.
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        const string line = "notes.txt — read from destination 'spare', around destination 'vault': "
            + "Record 1234 in blob 5678 failed authentication (specification 04 §7).";
        harness.Clients.Client.Respond = command => command is RunRestoreCommand
            ? new ApiRestoreResult(
                1, 0, "/restore/out", "complete", ReceiptPath: "/restore/out/receipt.json",
                ReadAround: 1, ReadAroundSample: [line])
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToThePlanAsync(page, harness);
        await harness.ReceivedAsync<PlanRestoreCommand>(plan => plan.Source == "src-1");
        await page.FillAsync("#confirm-word", "restore");
        await page.Locator("#rst-run-go").ClickAsync();

        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();
        await Expect(page.Locator("#dialog")).ToContainTextAsync("read from another copy");
        await Expect(page.Locator("#dialog pre.report")).ToContainTextAsync("around destination 'vault'");
    }

    [TestMethod]
    public async Task Wizard_ThePlanIsAskedForTheRunItPlans_AndShowsWhatItWritesAndTheRoomThereIs()
    {
        // FR-RST-003: the plan a person confirms is measured against the
        // folder the run will write to, so it is asked with the run's shape.
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new RestorePlanResult(
                1, 42, [], WriteBytes: 42,
                Space: [new RestoreSpaceDescriptor("/restore/out", 8_192, 5L * 1024 * 1024 * 1024)])
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToThePlanAsync(page, harness);

        var planned = await harness.ReceivedAsync<PlanRestoreCommand>(plan => plan.Source == "src-1");
        Assert.AreEqual("/restore/out", planned.OutputDirectory);
        Assert.AreEqual("folder", planned.Target);
        Assert.AreEqual("rename", planned.Existing);
        Assert.IsTrue(planned.InPlace);

        await Expect(page.Locator("#rst-space")).ToContainTextAsync("/restore/out");
        await Expect(page.Locator("#rst-space")).ToContainTextAsync("8.0 KiB");
        await Expect(page.Locator("#rst-space")).ToContainTextAsync("5.0 GiB free");
        await Expect(page.Locator("#rst-space-short")).ToHaveCountAsync(0);

        // Room enough: the word alone arms the restore, which asks for no
        // exception to the check.
        await page.FillAsync("#confirm-word", "restore");
        await page.Locator("#rst-run-go").ClickAsync();
        var restored = await harness.ReceivedAsync<RunRestoreCommand>();
        Assert.IsFalse(restored.IgnoreFreeSpace);
    }

    [TestMethod]
    public async Task Wizard_APlanThatWillNotFit_SaysSo_AndOnlyChoosingToRestoreAnywayArmsTheRestore()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new RestorePlanResult(
                1, 42, [], Conflicts: 1,
                ConflictSample: ["/restore/out — needs 976.6 KiB on this volume, which has 1000 B free"],
                WriteBytes: 42,
                Space: [new RestoreSpaceDescriptor("/restore/out", 1_000_000, 1_000)])
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToThePlanAsync(page, harness);
        await harness.ReceivedAsync<PlanRestoreCommand>(plan => plan.Source == "src-1");

        await Expect(page.Locator("#rst-space-short")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-space-short")).ToContainTextAsync("will not fit");

        // The word alone does not arm a restore the plan says will not fit.
        var run = page.Locator("#rst-run-go");
        await page.FillAsync("#confirm-word", "restore");
        await Expect(run).ToBeDisabledAsync();

        // A volume that compresses what it stores can hold more than the
        // estimate, so the person may choose to go on, and says so.
        await page.CheckAsync("#rst-ignore-space");
        await Expect(run).ToBeEnabledAsync();
        await run.ClickAsync();

        var restored = await harness.ReceivedAsync<RunRestoreCommand>();
        Assert.IsTrue(restored.IgnoreFreeSpace);
    }

    [TestMethod]
    public async Task Wizard_ARestoreThatDidNotApplyCapturedMetadata_SaysSoOnItsResult()
    {
        // FR-RST-004 where the person restoring is looking: what the files
        // came back without.
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        harness.Clients.Client.Respond = command => command is RunRestoreCommand
            ? new ApiRestoreResult(
                1, 0, "/restore/out", "complete", ReceiptPath: "/restore/out/receipt.json",
                NotApplied: ["accessed_at not applied to 1 item(s)"])
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToThePlanAsync(page, harness);
        await harness.ReceivedAsync<PlanRestoreCommand>(plan => plan.Source == "src-1");
        await page.FillAsync("#confirm-word", "restore");
        await page.Locator("#rst-run-go").ClickAsync();

        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-not-applied")).ToContainTextAsync("accessed_at not applied to 1 item(s)");
    }

    [TestMethod]
    public async Task Wizard_WhileThePlanIsOnItsWay_TheRestoreCannotBeArmed()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        var planReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Clients.Client.Respond = command =>
        {
            // Held at the fake until the step has been looked at without a
            // plan. A real plan probes every object the restore needs, and
            // from a peer's replica that takes as long as the link does.
            if (command is PlanRestoreCommand)
            {
                planReleased.Task.Wait(TimeSpan.FromSeconds(30));
            }

            return fakes(command);
        };

        try
        {
            await using var context = await BrowserSession.NewContextAsync();
            var page = await context.NewPageAsync();
            await WalkToThePlanAsync(page, harness);
            await harness.ReceivedAsync<PlanRestoreCommand>();

            // Nothing on screen says yet what the restore would do, so there
            // is nothing to confirm. The word typed here used to arm the
            // button, and then vanish with its field when the plan arrived
            // and re-rendered the step — or, clicked first, start a restore
            // whose plan nobody had read.
            var run = page.Locator("#rst-run-go");
            await Expect(page.GetByText("Planning…")).ToBeVisibleAsync();
            await Expect(page.Locator("#confirm-word")).ToBeDisabledAsync();
            await Expect(run).ToBeDisabledAsync();

            planReleased.SetResult();

            // The plan arrives with the one place to type, and focus already
            // in it: real keystrokes arm the button.
            await Expect(page.Locator(".plan-figures")).ToBeVisibleAsync();
            await Expect(page.Locator("#confirm-word")).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("restore");
            await Expect(run).ToBeEnabledAsync();
            await run.ClickAsync();

            var restored = await harness.ReceivedAsync<RunRestoreCommand>();
            Assert.AreEqual("snap-1", restored.SnapshotId);
        }
        finally
        {
            // A failure above must not leave the console's request parked on
            // the plan while the harness shuts down around it.
            planReleased.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Wizard_ARefusedPlan_SaysSo_AndTheRestoreCannotBeArmed()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new ServiceError(ServiceErrorReason.Failed, "The snapshot's manifest could not be read.")
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToThePlanAsync(page, harness);

        // No plan is coming, and the step says so rather than "Planning…"
        // for ever; with no plan there is nothing to confirm, so the way on
        // stays shut. Back is the way out, and continuing again re-plans.
        await Expect(page.GetByText("No plan could be made")).ToBeVisibleAsync();
        await Expect(page.GetByText("Planning…")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#confirm-word")).ToBeDisabledAsync();
        await Expect(page.Locator("#rst-run-go")).ToBeDisabledAsync();
        await Expect(page.Locator("#dialog [data-action=\"rst-back\"]")).ToBeEnabledAsync();
    }

    [TestMethod]
    public async Task Wizard_TheWrongPassphrase_IsRefused_AndOpensNothing()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = WizardFakes(now);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", "not the passphrase");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // The refusal is a derivation that does not reproduce the published
        // sealing key, not a string comparison — and the wizard stays on
        // step 1 with nothing opened.
        await Expect(page.GetByText("That passphrase does not open the repository.")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-set")).Not.ToBeVisibleAsync();
        lock (harness.Clients.Client.Received)
        {
            Assert.IsEmpty(harness.Clients.Client.Received.OfType<OpenRestoreSourceCommand>());
        }
    }

    [TestMethod]
    public async Task Wizard_WhereThePassphraseCannotBeChecked_ThereIsNoWayThrough()
    {
        // A service that publishes nothing to derive under — not yet set up —
        // cannot have its passphrase checked, and an unchecked passphrase is
        // no passphrase at all: the wizard says why and goes no further.
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now);
        harness.Clients.Client.Respond = command => command is DescribeServiceCommand
            ? Wire.Describe("ready", signedInUser: "owner") with
            {
                RestoreGrantRecipient = null, KdfSalt = null, SealingPublicKey = null,
            }
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", "anything at all");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        await Expect(page.GetByText("cannot be checked")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-gate-ack")).ToHaveCountAsync(0);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-set")).Not.ToBeVisibleAsync();
        lock (harness.Clients.Client.Received)
        {
            Assert.IsEmpty(harness.Clients.Client.Received.OfType<OpenRestoreSourceCommand>());
        }
    }

    /// <summary>A computed style of the name in the file tree's row for <paramref name="path"/>.</summary>
    private static Task<string> EntryStyleAsync(IPage page, string path, string property) =>
        page.Locator($"#rst-tree .tree-row:has(input[data-rst-mark=\"{path}\"]) .tree-name")
            .EvaluateAsync<string>("(name, property) => getComputedStyle(name)[property]", property);
}
