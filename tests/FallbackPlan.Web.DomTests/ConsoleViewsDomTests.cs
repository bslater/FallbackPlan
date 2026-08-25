using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The hash-routed views, driven by real clicks against faked service
/// answers: what each view renders from its result records, and that its
/// actions send the commands they claim to.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class ConsoleViewsDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task Overview_RendersTheSetCard_AndBackupNowQueuesAJob()
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
                        "docs",
                        new BackupSetStatus(ProtectionState.Protected, null, []),
                        NextRun: null,
                        Destinations:
                        [
                            new DestinationStatusDescriptor(
                                "vault", "local-path", "in-sync", now, null, "independent", "verified"),
                        ]),
                ],
                now,
                []),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            RunBackupCommand => new JobAcceptedResult("job-9"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        // The card renders the protection vocabulary, not raw enum numbers —
        // which also pins that the host serializes enums by name.
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();
        await Expect(page.Locator("#view-overview").GetByText("Protected")).ToBeVisibleAsync();

        await page.ClickAsync("[data-action=\"backup\"]");
        var queued = await harness.ReceivedAsync<RunBackupCommand>();
        Assert.AreEqual("docs", queued.SetName);
        Assert.IsFalse(queued.Full);

        // The action announces itself and moves the person to where the work
        // now is.
        await Expect(page.GetByText("queued as job job-9")).ToBeVisibleAsync();
        Assert.AreEqual("#jobs", await page.EvaluateAsync<string>("location.hash"));
    }

    [TestMethod]
    public async Task Snapshots_RenderTheCaptureVocabulary_AndBrowseOpensTheListing()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListSnapshotsCommand => new SnapshotsResult([Wire.Snapshot(now)]),
            ListDirectoryCommand => new DirectoryResult(
                "",
                [
                    new DirectoryEntryDescriptor("notes.txt", "file", 42, now, "changed"),
                    new DirectoryEntryDescriptor("photos", "directory", 0),
                ],
                Deleted: ["old-report.pdf"],
                PreviousSnapshotId: "snap-0"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        // The capture column speaks the normative vocabulary: complete, and
        // the consistency method's name — the surfacing this branch added.
        await Expect(page.Locator("#view-snapshots").GetByText("complete")).ToBeVisibleAsync();
        await Expect(page.Locator("#view-snapshots").GetByText("live capture")).ToBeVisibleAsync();

        await page.ClickAsync("[data-action=\"browse\"]");
        var listed = await harness.ReceivedAsync<ListDirectoryCommand>();
        Assert.AreEqual("snap-1", listed.SnapshotId);

        // The listing dialog: entries, the change badge, and deletion shown
        // as absence between snapshots.
        var dialog = page.Locator("#dialog");
        await Expect(dialog.GetByText("notes.txt")).ToBeVisibleAsync();
        await Expect(dialog.GetByText("old-report.pdf")).ToBeVisibleAsync();
        await page.ClickAsync("#dialog [data-action=\"close-dialog\"]");
    }

    [TestMethod]
    public async Task Notices_AcknowledgeEmptiesTheList()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        var acknowledged = false;
        harness.Clients.Client.Respond = command =>
        {
            switch (command)
            {
                case DescribeServiceCommand:
                    return Wire.Describe("ready", signedInUser: "owner");
                case ListNoticesCommand:
                    return new NoticesResult(acknowledged
                        ? []
                        : [new NoticeDescriptor("n-1", "replica-behind", "Replica 'vault' is behind.", now, null)]);
                case AcknowledgeNoticeCommand:
                    acknowledged = true;
                    return new AcknowledgedResult();
                default:
                    return new AcknowledgedResult();
            }
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#notices");

        await Expect(page.GetByText("Replica 'vault' is behind.")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"notice-ack\"]");

        var ack = await harness.ReceivedAsync<AcknowledgeNoticeCommand>();
        Assert.AreEqual("n-1", ack.Id);
        await Expect(page.GetByText("Nothing awaits you.")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Diagnostics_RenderTheRing_AndSetLevelSendsTheCommand()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetDiagnosticsCommand => new DiagnosticsResult(
                "information",
                new Dictionary<string, string> { ["FallbackPlan.Repository"] = "debug" },
                DurableSink: true, RetainFiles: 5, MaximumFileBytes: 8 * 1024 * 1024,
                RingCapacity: 2048, OldestSequence: 0, NextSequence: 2),
            ReadLogCommand => new LogRecordsResult(
                [
                    new LogRecordDescriptor(
                        1, (long)now, "information", 3730, "FallbackPlan.Agent.AgentHost",
                        "Service listening on the local binding"),
                ],
                NextSequence: 2, Dropped: false),
            SetLogLevelCommand => new ConfigurationChangeResult(["Default level is now trace."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#diagnostics");

        await Expect(page.GetByText("Service listening on the local binding")).ToBeVisibleAsync();
        Assert.AreEqual("information", await page.InputValueAsync("#diag-level"));

        await page.SelectOptionAsync("#diag-level", "trace");
        await page.ClickAsync("[data-action=\"set-log-level\"]");
        var levelled = await harness.ReceivedAsync<SetLogLevelCommand>();
        Assert.AreEqual("trace", levelled.Level);
        Assert.IsNull(levelled.Category);
        await Expect(page.GetByText("Default level is now trace.")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task JobsMeter_MovesOnProgressEvents()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListJobsCommand => new JobsResult(
                [new JobDescriptor("job-1", Wire.SetId, JobState.Publishing, now, now, null, null)]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set("users")]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        // A real SSE frame through the console's event bridge: the badge
        // follows the stream's state and the meter's width follows the maths
        // (40 done + 10 reused of 100 seen = 50%).
        harness.Clients.Client.Emit(new JobProgress("job-1", JobState.Packing, 100, 40, 10, 0, 1024, 512));

        await Expect(page.Locator(".job-live").GetByText("Packing")).ToBeVisibleAsync();
        await Expect(page.Locator(".job-live .meter > i")).ToHaveAttributeAsync("data-w", "50");
        await Expect(page.GetByText("512 B")).ToBeVisibleAsync();
    }
}
