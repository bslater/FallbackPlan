using System.Text;
using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console runs a kept plan as it was approved (FR-RST-003, ADR-0118),
/// in a real browser. A plan kept by either client is read by the console
/// process, which tells a file that is no kept plan by name. The passphrase
/// opens a source of the plan's own set, and the console plans the kept run
/// again through it. The run is offered, under the typed word, only where
/// nothing the plan said has changed, and asked exactly as it was kept;
/// otherwise each change is named and nothing can be run. A backup no set
/// here holds, or a source that does not hold it, runs nothing. Where the
/// plan made now counts files a folder would open wider, the run is armed
/// only once the person acknowledges it (FR-RST-003, ADR-0178 Amendment 2).
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreKeptPlanDomTests
{
    private static readonly ulong Now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly PlanRestoreCommand KeptRun = new(
        "snap-1", null, Paths: ["notes.txt", "photos"], OutputDirectory: "/restore/out", Target: "folder",
        Existing: "rename", InPlace: true);

    private static readonly RestorePlanResult KeptPlan = new(2, 42, []);

    private static byte[] KeptFile(PlanRestoreCommand? run = null) => Encoding.UTF8.GetBytes(
        RestorePlanFile.Serialize(run ?? KeptRun, KeptPlan, DateTimeOffset.UtcNow.AddHours(-2)));

    private static Func<ServiceCommand, ServiceResult> Fakes(
        RestorePlanResult? planNow = null, IReadOnlyList<SnapshotDescriptor>? held = null) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set("other", new string('b', 32)), Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListSnapshotsCommand => new SnapshotsResult([Wire.Snapshot(Now)]),
            OpenRestoreSourceCommand => new RestoreSourceOpenedResult(
                "src-1", "docs", "staging", held ?? [Wire.Snapshot(Now)], []),
            PlanRestoreCommand => planNow ?? KeptPlan,
            RunRestoreCommand => new ApiRestoreResult(2, 0, "/restore/out", "complete", Run: "0123456789abcdef"),
            _ => new AcknowledgedResult(),
        };

    [TestMethod]
    public async Task AKeptPlan_IsRunAsItWasApproved_WhereNothingHasChanged()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheKeptPlanAsync(page, harness, KeptFile());

        // Planned again as the kept run, through the source just opened.
        var planned = await harness.ReceivedAsync<PlanRestoreCommand>();
        Assert.AreEqual("src-1", planned.Source);
        CollectionAssert.AreEqual(new[] { "notes.txt", "photos" }, planned.Paths!.ToArray());
        Assert.AreEqual("rename", planned.Existing);

        await Expect(page.Locator("#rst-kept-summary")).ToContainTextAsync("as kept");
        var run = page.Locator("#rst-kept-go");
        await Expect(run).ToBeDisabledAsync();
        await page.FillAsync("#confirm-word", "restore");
        await Expect(run).ToBeEnabledAsync();
        await run.ClickAsync();

        var restored = await harness.ReceivedAsync<RunRestoreCommand>();
        Assert.AreEqual("snap-1", restored.SnapshotId);
        Assert.AreEqual("src-1", restored.Source);
        CollectionAssert.AreEqual(new[] { "notes.txt", "photos" }, restored.Paths!.ToArray());
        Assert.AreEqual("/restore/out", restored.OutputDirectory);
        Assert.AreEqual("folder", restored.Target);
        Assert.AreEqual("rename", restored.Existing);
        Assert.IsTrue(restored.InPlace);
        Assert.IsFalse(restored.AcknowledgeWiderAccess);
        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task AKeptPlanWhoseFolderWouldOpenFilesWider_IsRunOnlyWhenThePersonAcknowledgesIt()
    {
        // What the plan made now says of the folder is the run's to refuse
        // (ADR-0178 Amendment 1), whatever was kept, so the person says so.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(planNow: KeptPlan with { OpenedWider = 1 });

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheKeptPlanAsync(page, harness, KeptFile());

        await Expect(page.Locator("#rst-kept-summary")).ToContainTextAsync("as kept");
        await Expect(page.Locator("#rst-wider")).ToContainTextAsync("1 file(s)");
        var run = page.Locator("#rst-kept-go");
        await page.FillAsync("#confirm-word", "restore");
        await Expect(run).ToBeDisabledAsync();
        await page.CheckAsync("#rst-ack-wider");
        await Expect(run).ToBeEnabledAsync();
        await run.ClickAsync();

        Assert.IsTrue((await harness.ReceivedAsync<RunRestoreCommand>()).AcknowledgeWiderAccess);
    }

    [TestMethod]
    public async Task AKeptPlanThatHasChanged_NamesEachChange_AndOffersNothingToRun()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(planNow: new RestorePlanResult(1, 42, ["notes.txt"]));

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheKeptPlanAsync(page, harness, KeptFile());

        var changes = page.Locator("#rst-kept-changes");
        await Expect(changes).ToContainTextAsync("missing now: notes.txt");
        await Expect(changes).ToContainTextAsync("files: 2 kept, 1 now");
        await Expect(page.Locator("#rst-kept-go")).ToHaveCountAsync(0);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RunRestoreCommand>());
    }

    [TestMethod]
    public async Task AFileThatIsNotAKeptPlan_IsRefused_SayingWhy_AndOpensNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await ChooseAsync(page, Encoding.UTF8.GetBytes("""{"kind":"something-else","version":1}"""));

        await Expect(page.GetByText("not a kept restore plan")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-passphrase")).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task AKeptPlanOfABackupNoSetHereHolds_OpensNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await ChooseAsync(page, KeptFile(KeptRun with { SnapshotId = "snap-elsewhere" }));

        await Expect(page.GetByText("no backup here")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-passphrase")).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task ASourceThatDoesNotHoldTheKeptBackup_PlansNothing_AndRunsNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(held: [Wire.Snapshot(Now, id: "snap-2")]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await ChooseAsync(page, KeptFile());
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        await Expect(page.GetByText("does not hold the backup this plan restores")).ToBeVisibleAsync();
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RunRestoreCommand>());
    }

    private static async Task WalkToTheKeptPlanAsync(IPage page, DomHarness harness, byte[] file)
    {
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await ChooseAsync(page, file);
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // The plan's own set, fixed: the run is of the backup it was kept for.
        await Expect(page.Locator("#rst-set")).ToHaveValueAsync("docs");
        await Expect(page.Locator("#rst-set")).ToBeDisabledAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        _ = await harness.ReceivedAsync<OpenRestoreSourceCommand>();
    }

    private static Task ChooseAsync(IPage page, byte[] file) =>
        page.SetInputFilesAsync("#kept-plan-file", new FilePayload { Name = "approved.json", MimeType = "application/json", Buffer = file });
}
