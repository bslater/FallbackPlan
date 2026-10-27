using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;
using ApiRestoreResult = FallbackPlan.Api.RestoreResult;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console resumes a restore it did not see end (FR-RST-008, ADR-0116),
/// in a real browser. The snapshots page names each restore the service
/// began and has not seen finish, with its set, snapshot and destination and
/// none of its files, and so does the notice the service raised for it.
/// Restoring what is left asks the passphrase, opens a source of the run's
/// own set, and has the service run it again by its name alone; a resume
/// that does not finish offers to resume again under the name its result
/// gives, and going back from the resume returns to its source. A run can be
/// let go. A source that does not hold the run's backup resumes nothing, and a
/// service before contract 1.71 names no run. Before it runs, the resume is
/// planned (ADR-0121): what the run already landed, what is left to write,
/// and whether it fits, and one that will not fit is armed only when the
/// person says to go on; a service that cannot plan it still resumes it.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RestoreUnfinishedDomTests
{
    private const string Run = "0123456789abcdef";

    private static readonly ulong Now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly RestoreRunDescriptor Unfinished =
        new(Run, "snap-1", "docs", "/restore/out", Now - 3_600_000, Existing: "rename", InPlace: true);

    /// <summary>A resume's plan that fits, one file already landed.</summary>
    private static readonly RestorePlanResult Fits = new(
        2, 42, [], WriteBytes: 21, Space: [new RestoreSpaceDescriptor("/restore/out", 8_192, 1L << 30, false)], Landed: 1);

    private static Func<ServiceCommand, ServiceResult> Fakes(
        Func<ResumeRestoreCommand, ServiceResult>? resume = null, IReadOnlyList<SnapshotDescriptor>? held = null,
        Func<PlanResumeRestoreCommand, ServiceResult>? plan = null) =>
        command => command switch
        {
            PlanResumeRestoreCommand asked => plan?.Invoke(asked) ?? Fits,
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set("other", new string('b', 32)), Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListSnapshotsCommand => new SnapshotsResult([Wire.Snapshot(Now)]),
            ListRestoreRunsCommand => new RestoreRunsResult([Unfinished]),
            OpenRestoreSourceCommand => new RestoreSourceOpenedResult(
                "src-1", "docs", "staging", held ?? [Wire.Snapshot(Now)], []),
            ResumeRestoreCommand asked => resume?.Invoke(asked) ?? new ApiRestoreResult(
                2, 0, $"/restore/out/.fbp-quarantine/{Run}", "complete", FoundInPlace: 1, Run: Run),
            _ => new AcknowledgedResult(),
        };

    [TestMethod]
    public async Task TheSnapshotsPage_NamesEachRestoreLeftUnfinished_WithoutItsFiles()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var card = page.Locator("#unfinished-runs");
        await Expect(card).ToBeVisibleAsync();
        await Expect(card).ToContainTextAsync(Run);
        await Expect(card).ToContainTextAsync("docs");
        await Expect(card).ToContainTextAsync("/restore/out");
        await Expect(card).ToContainTextAsync("in place");
        await Expect(card.Locator($"[data-action=\"resume-run\"][data-run=\"{Run}\"]")).ToBeVisibleAsync();
        await Expect(card.Locator($"[data-action=\"forget-run\"][data-run=\"{Run}\"]")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ARunLeftUnfinished_IsResumedByItsNameAlone_ThroughASourceOfItsOwnSet()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await page.ClickAsync($"[data-action=\"resume-run\"][data-run=\"{Run}\"]");
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        // The run's set, not the first configured: the resume reads the
        // backup the run was asked of.
        await Expect(page.Locator("#rst-set")).ToHaveValueAsync("docs");
        await Expect(page.Locator("#rst-set")).ToBeDisabledAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        var opened = await harness.ReceivedAsync<OpenRestoreSourceCommand>();
        Assert.AreEqual("docs", opened.SetName);
        Assert.IsTrue(Wire.IsTheInstallationsGrant(opened.Envelope), "the source opens under the set's grant");

        // Straight to the resume: the run is asked as it was, so there is no
        // date, file or target to choose.
        var summary = page.Locator("#rst-resume-summary");
        await Expect(summary).ToContainTextAsync(Run);
        await Expect(summary).ToContainTextAsync("/restore/out");

        // Planned first, by the run's name, through the source just opened.
        var planned = await harness.ReceivedAsync<PlanResumeRestoreCommand>();
        Assert.AreEqual(Run, planned.Run);
        Assert.AreEqual("src-1", planned.Source);
        await Expect(page.Locator("#rst-landed")).ToContainTextAsync("already landed");
        await Expect(page.Locator("#rst-landed b")).ToHaveTextAsync("1");
        await Expect(page.Locator("#rst-space")).ToContainTextAsync("/restore/out");
        await Expect(page.Locator("#rst-space-short")).ToHaveCountAsync(0);
        await page.ClickAsync("#rst-resume-go");

        var resumed = await harness.ReceivedAsync<ResumeRestoreCommand>();
        Assert.AreEqual(Run, resumed.Run);
        Assert.AreEqual("src-1", resumed.Source);
        Assert.IsFalse(resumed.IgnoreFreeSpace);
        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-found-in-place")).ToBeVisibleAsync();
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RunRestoreCommand>(), "the service asks the run again, not the page");
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
    }

    [TestMethod]
    public async Task AResumeThatWillNotFit_IsAskedToGoOn_OnlyWhenThePersonSaysSo()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(plan: _ => Fits with
        {
            Space = [new RestoreSpaceDescriptor("/restore/out", 8_192, 100, false)],
        });

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheResumeAsync(page, harness);

        await Expect(page.Locator("#rst-space-short")).ToBeVisibleAsync();
        var go = page.Locator("#rst-resume-go");
        await Expect(go).ToBeDisabledAsync();
        await page.CheckAsync("#rst-ignore-space");
        await Expect(go).ToBeEnabledAsync();
        await go.ClickAsync();

        Assert.IsTrue((await harness.ReceivedAsync<ResumeRestoreCommand>()).IgnoreFreeSpace);
    }

    [TestMethod]
    public async Task AResumeNamingFilesThatCannotComeBack_SaysSoBeforeItRuns()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(plan: _ => Fits with { MissingObjects = ["notes.txt"] });

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheResumeAsync(page, harness);

        await Expect(page.Locator("#dialog")).ToContainTextAsync("cannot be found");
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ResumeRestoreCommand>(), "a plan resumes nothing");
    }

    [TestMethod]
    public async Task AResumeTheServiceCannotPlan_IsStillOffered_AsBefore()
    {
        // Before contract 1.72 the service does not know the command.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(plan: _ => new ServiceError(ServiceErrorReason.InvalidArgument, "Unknown command."));

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheResumeAsync(page, harness);

        await Expect(page.Locator("#rst-resume-go")).ToBeEnabledAsync();
        await Expect(page.Locator("#rst-landed")).ToHaveCountAsync(0);
        await page.ClickAsync("#rst-resume-go");

        Assert.AreEqual(Run, (await harness.ReceivedAsync<ResumeRestoreCommand>()).Run);
    }

    [TestMethod]
    public async Task BackFromTheResume_ReturnsToItsSource_NotToStepsItNeverTook()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheResumeAsync(page, harness);

        await page.ClickAsync("#dialog [data-action=\"rst-back\"]");

        await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-output")).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task AResumeThatDoesNotFinishAgain_OffersToResumeIt_UnderTheNameItsResultGives()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(resume: asked => asked.Run == Run
            ? new ApiRestoreResult(1, 1, "/restore/out", "failed", Run: "fedcba9876543210")
            : new ApiRestoreResult(2, 0, "/restore/out", "complete", Run: asked.Run));

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await WalkToTheResumeAsync(page, harness);
        await page.ClickAsync("#rst-resume-go");

        await page.ClickAsync("#rst-resume");

        var again = await harness.ReceivedAsync<ResumeRestoreCommand>(asked => asked.Run == "fedcba9876543210");
        Assert.AreEqual("src-1", again.Source);
        await Expect(page.GetByText("Restore complete")).ToBeVisibleAsync();
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RunRestoreCommand>());
    }

    [TestMethod]
    public async Task ASourceThatDoesNotHoldTheRunsBackup_SaysSo_AndResumesNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes(held: [Wire.Snapshot(Now, id: "snap-2")]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await page.ClickAsync($"[data-action=\"resume-run\"][data-run=\"{Run}\"]");
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await page.ClickAsync("[data-action=\"rst-continue\"]");

        await Expect(page.GetByText("does not hold the backup this run restores")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-resume-go")).ToHaveCountAsync(0);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<ResumeRestoreCommand>());
    }

    [TestMethod]
    public async Task ARunLeftUnfinished_CanBeLetGo()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Fakes();

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await page.ClickAsync($"[data-action=\"forget-run\"][data-run=\"{Run}\"]");
        await Expect(page.Locator("#dialog")).ToContainTextAsync("stays where it landed");
        await page.ClickAsync("#forget-run-go");

        Assert.AreEqual(Run, (await harness.ReceivedAsync<ForgetRestoreRunCommand>()).Run);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<OpenRestoreSourceCommand>(), "letting a run go asks no passphrase");
    }

    [TestMethod]
    public async Task TheNoticeOfARunLeftUnfinished_OffersToRestoreWhatIsLeft()
    {
        await using var harness = await DomHarness.StartAsync();
        var fakes = Fakes();
        harness.Clients.Client.Respond = command => command is ListNoticesCommand
            ? new NoticesResult([new NoticeDescriptor("n-1", $"restore-unfinished:{Run}", "A restore did not finish.", Now, null)])
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#notices");

        await page.ClickAsync($"[data-action=\"resume-run\"][data-run=\"{Run}\"]");

        await Expect(page.Locator("#rst-passphrase")).ToBeVisibleAsync();
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-set")).ToHaveValueAsync("docs");
    }

    [TestMethod]
    public async Task ARunNoLongerLeftUnfinished_OpensNothing_AndSaysSo()
    {
        // A notice outlives the run it names until it is resolved, and the
        // run may have finished in another tab meanwhile.
        await using var harness = await DomHarness.StartAsync();
        var fakes = Fakes();
        harness.Clients.Client.Respond = command => command switch
        {
            ListNoticesCommand => new NoticesResult(
                [new NoticeDescriptor("n-1", $"restore-unfinished:{Run}", "A restore did not finish.", Now, null)]),
            ListRestoreRunsCommand => new RestoreRunsResult([]),
            _ => fakes(command),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#notices");

        await page.ClickAsync($"[data-action=\"resume-run\"][data-run=\"{Run}\"]");

        await Expect(page.GetByText("no longer left unfinished")).ToBeVisibleAsync();
        await Expect(page.Locator("#rst-passphrase")).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task AServiceThatNamesNoRun_ShowsNone()
    {
        // Before contract 1.71 the service does not know the command.
        await using var harness = await DomHarness.StartAsync();
        var fakes = Fakes();
        harness.Clients.Client.Respond = command => command is ListRestoreRunsCommand
            ? new ServiceError(ServiceErrorReason.InvalidArgument, "Unknown command.")
            : fakes(command);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        await Expect(page.Locator("[data-action=\"restore\"]").First).ToBeVisibleAsync();
        await Expect(page.Locator("#unfinished-runs")).ToHaveCountAsync(0);
    }

    private static async Task WalkToTheResumeAsync(IPage page, DomHarness harness)
    {
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await page.ClickAsync($"[data-action=\"resume-run\"][data-run=\"{Run}\"]");
        await page.FillAsync("#rst-passphrase", Wire.Passphrase);
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-set")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"rst-continue\"]");
        await Expect(page.Locator("#rst-resume-go")).ToBeVisibleAsync();
    }
}
