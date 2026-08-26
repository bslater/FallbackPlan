using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The guided restore wizard (ADR-0041) walked end to end in a real browser —
/// the committed counterpart of the "live Playwright walk" ADR-0041 cites.
/// The passphrase gate is the real thing: a v1 archive on disk, the console
/// process deriving with Argon2id against its key files, the secret never on
/// the service wire (NFR-SEC-009).
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
        (await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(archive), right, RepositoryCreationSettings.Default,
            1_722_700_000_000UL, CancellationToken.None)).Dispose();
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
