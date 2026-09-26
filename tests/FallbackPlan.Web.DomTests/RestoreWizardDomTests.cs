using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The guided restore wizard (ADR-0041) walked end to end in a real browser —
/// the committed counterpart of the "live Playwright walk" ADR-0041 cites.
/// The passphrase gate is the real thing: a v1 archive on disk, the console
/// process deriving with Argon2id against its key files, the secret never on
/// the service wire (NFR-SEC-009). The last step confirms a plan, so nothing
/// there can be confirmed until the plan is on screen — the console's half of
/// FR-RST-003, which puts the plan before any byte is written.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreWizardDomTests
{
    private const string RightPassphrase = "the right passphrase!!";

    private static string _archives = null!;

    [ClassInitialize]
    public static async Task CreateTheArchiveAsync(TestContext context)
    {
        _ = context;
        if (Environment.GetEnvironmentVariable(BrowserConditionAttribute.Variable) != "1")
        {
            return; // condition-skipped runs must not pay the derivation either
        }

        // One real v1 archive for the whole class: the gate verifies against
        // whichever archive under the root will answer, keyed by directory.
        _archives = Path.Combine(Path.GetTempPath(), "fbp-dom-gate", Guid.NewGuid().ToString("n")[..12]);
        var archive = Path.Combine(_archives, Wire.SetId);
        Directory.CreateDirectory(archive);
        using var right = Passphrase.Create(RightPassphrase);

        // Re-homed onto the credential-taking CreateAsync this branch settled
        // on: the passphrase overload went with KeyHierarchy. The salt is
        // fixed so the archive these tests gate against is reproducible.
        var salt = Enumerable.Repeat((byte)0x5A, KekDerivation.SaltLength).ToArray();
        using var authority = WriteOnlyDerivation.Derive(
            right, RepositoryCreationSettings.Default.KdfParameters, salt,
            KdfValidationMode.CreateRepository);
        (await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(archive), authority.Credential,
            salt, RepositoryCreationSettings.Default.KdfParameters,
            createdBy: "dom-tests", 1_722_700_000_000UL, CancellationToken.None)).Dispose();
    }

    [ClassCleanup]
    public static void DeleteTheArchive()
    {
        try
        {
            if (_archives is not null)
            {
                Directory.Delete(_archives, recursive: true);
            }
        }
        catch (IOException)
        {
            // A straggling handle on a temp directory is not a test failure.
        }
    }

    private static Func<ServiceCommand, ServiceResult> WizardFakes(ulong now, string archivesRoot) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner", archivesRoot: archivesRoot),
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
        harness.Clients.Client.Respond = WizardFakes(now, _archives);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        // Step 1 — unlock: the typed passphrase is checked by the console
        // process against the archive on disk. A real derivation runs here.
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
    public async Task Wizard_WhileThePlanIsOnItsWay_TheRestoreCannotBeArmed()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var fakes = WizardFakes(now, _archives);
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
        var fakes = WizardFakes(now, _archives);
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
    public async Task Wizard_TheWrongPassphrase_IsRefusedByTheLocalGate()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = WizardFakes(now, _archives);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await page.ClickAsync("[data-action=\"restore\"]");
        await page.FillAsync("#rst-passphrase", "not the passphrase");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // The refusal is the archive's own: a failed unwrap against real key
        // files, not a string comparison — and the wizard stays on step 1.
        await Expect(page.GetByText("That passphrase does not open the repository.")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-set")).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Wizard_WithNoLocalArchive_OffersTheAcknowledgedWayThrough()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var empty = Path.Combine(Path.GetTempPath(), "fbp-dom-gate", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(empty);
        try
        {
            await using var harness = await DomHarness.StartAsync();
            harness.Clients.Client.Respond = WizardFakes(now, empty);

            await using var context = await BrowserSession.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

            await page.ClickAsync("[data-action=\"restore\"]");
            await page.FillAsync("#rst-passphrase", "anything at all");
            await page.ClickAsync("[data-action=\"rst-continue\"]");

            // Nothing local to verify against: the gate says so honestly and
            // asks for an explicit acknowledgement instead of pretending.
            await Expect(page.Locator("#rst-gate-ack")).ToBeVisibleAsync();
            await page.CheckAsync("#rst-gate-ack");
            await page.ClickAsync("[data-action=\"rst-continue\"]");

            await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(empty, recursive: true);
            }
            catch (IOException)
            {
                // A straggling handle on a temp directory is not a test failure.
            }
        }
    }
}
