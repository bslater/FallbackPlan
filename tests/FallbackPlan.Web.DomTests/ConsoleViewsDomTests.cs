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
    public async Task Snapshots_SayHowFarTheCapturingClockStoodFromItsPeer()
    {
        // Contract 1.47 (NFR-TIME-002): three hours behind its peer is drawn
        // as a warning, a clock in step says so, and a capture with no peer
        // to compare with draws nothing at all.
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListSnapshotsCommand => new SnapshotsResult(
            [
                Wire.Snapshot(now, "snap-3", observedClockSkewMs: 10_800_000),
                Wire.Snapshot(now - 60_000, "snap-2", observedClockSkewMs: 400),
                Wire.Snapshot(now - 120_000, "snap-1"),
            ]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var view = page.Locator("#view-snapshots");
        await Expect(view.Locator("b.warn", new() { HasText = "clock 3 h behind its peer" })).ToBeVisibleAsync();
        await Expect(view.GetByText("clock in step with its peer")).ToBeVisibleAsync();
        var unobserved = view.Locator("tr", new() { HasText = "snap-1" });
        await Expect(unobserved).ToHaveCountAsync(1);
        await Expect(unobserved).Not.ToContainTextAsync("clock");
    }

    [TestMethod]
    public async Task Snapshots_SayWhenACaptureTimeDoesNotFitItsPublicationOrder()
    {
        // Contract 1.48 (FR-GC-012): a capture a wrong clock misdated is drawn
        // as a warning naming which way it is out of step, and a capture that
        // fits draws nothing.
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListSnapshotsCommand => new SnapshotsResult(
            [
                Wire.Snapshot(now, "snap-3"),
                Wire.Snapshot(978_307_200_000, "snap-2", implausibleCaptureTime: "behind"),
                Wire.Snapshot(now - 120_000, "snap-1"),
            ]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var view = page.Locator("#view-snapshots");
        var misdated = view.Locator("tr", new() { HasText = "snap-2" });
        await Expect(misdated.Locator("b.warn", new() { HasText = "dated before snapshots taken ahead of it" }))
            .ToBeVisibleAsync();
        var fits = view.Locator("tr", new() { HasText = "snap-3" });
        await Expect(fits).ToHaveCountAsync(1);
        await Expect(fits).Not.ToContainTextAsync("implausible");
    }

    [TestMethod]
    public async Task Snapshots_AreListedNewestFirst_AcrossEverySet()
    {
        // list_snapshots answers each set newest first, one set after
        // another. The view is one timeline of every set, the newest capture
        // on top whichever set took it; the filter narrows it and keeps the
        // order.
        var now = NowMs;
        var photos = new string('b', 32);
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set(), Wire.Set("photos", photos)]),
            ListSnapshotsCommand => new SnapshotsResult(
            [
                Wire.Snapshot(now, "docs-2"),
                Wire.Snapshot(now - 120_000, "docs-1"),
                Wire.Snapshot(now - 60_000, "photos-2", setId: photos),
                Wire.Snapshot(now - 180_000, "photos-1", setId: photos),
            ]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var rows = page.Locator("#view-snapshots tbody tr");
        await Expect(rows).ToHaveCountAsync(4);
        string[] timeline = ["docs-2", "photos-2", "docs-1", "photos-1"];
        for (var i = 0; i < timeline.Length; i++)
        {
            await Expect(rows.Nth(i)).ToContainTextAsync(timeline[i]);
        }

        await page.SelectOptionAsync("#snapshot-filter", Wire.SetId);
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.Nth(0)).ToContainTextAsync("docs-2");
        await Expect(rows.Nth(1)).ToContainTextAsync("docs-1");
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
    public async Task Notices_AClaimsNotice_CarriesItsAcknowledgement_ForTheReplicaItNames()
    {
        // FR-DR-005: the notice a claim raises asks a question its own
        // Acknowledge does not answer — that only dismisses it. The Owner is
        // offered the claim's acknowledgement beside it, for the replica the
        // notice's key names.
        var repositoryId = new string('d', 32);
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner") with { SignedInRole = "Owner" },
            ListNoticesCommand => new NoticesResult(
                [new NoticeDescriptor("n-1", $"replica-claimed:{repositoryId}", "'rebuilt-laptop' claimed a replica.", NowMs, null)]),
            AcknowledgeReplicaClaimCommand => new ConfigurationChangeResult(["Acknowledged the claim."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#notices");

        await Expect(page.GetByText("'rebuilt-laptop' claimed a replica.")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"claim-ack-open\"]");
        await page.FillAsync("#confirm-word", "acknowledge");
        await page.ClickAsync("#claim-ack-go");

        var sent = await harness.ReceivedAsync<AcknowledgeReplicaClaimCommand>();
        Assert.AreEqual(repositoryId, sent.RepositoryId);
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
    public async Task Diagnostics_DownloadABundle_SavesTheFileAndPathsAreAnOptInWithItsConsequence()
    {
        // Contract 1.52 (ADR-0081, NFR-PRIV-003): the bundle downloads as the
        // file the service built, and paths go in only when a person ticks for
        // them, having been told what that means.
        byte[] content = [0x50, 0x4b, 0x05, 0x06, 0x00, 0x00, 0x2a];
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetDiagnosticsCommand => new DiagnosticsResult(
                "information", new Dictionary<string, string>(),
                DurableSink: true, RetainFiles: 5, MaximumFileBytes: 8 * 1024 * 1024,
                RingCapacity: 2048, OldestSequence: 0, NextSequence: 0),
            ReadLogCommand => new LogRecordsResult([], NextSequence: 0, Dropped: false),
            ExportDiagnosticsCommand export => new DiagnosticBundleResult(
                "fallbackplan-diagnostics-20261004-120000Z.zip", Convert.ToBase64String(content), export.IncludePaths,
                ["README.txt", "log.txt"], LogRecords: 3, LogRecordsLeftOut: 0),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#diagnostics");

        await Expect(page.Locator("[data-action=\"export-diagnostics\"]")).ToBeVisibleAsync();
        await Expect(page.Locator("#diag-bundle-warning")).ToBeHiddenAsync();

        var download = await page.RunAndWaitForDownloadAsync(
            () => page.ClickAsync("[data-action=\"export-diagnostics\"]"));
        var plain = await harness.ReceivedAsync<ExportDiagnosticsCommand>();
        Assert.IsFalse(plain.IncludePaths);
        Assert.AreEqual("fallbackplan-diagnostics-20261004-120000Z.zip", download.SuggestedFilename);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync((await download.PathAsync())!));

        // Ticking says what the tick costs, before anything is sent.
        await page.CheckAsync("#diag-bundle-paths");
        await Expect(page.Locator("#diag-bundle-warning")).ToBeVisibleAsync();
        await Expect(page.Locator("#diag-bundle-warning")).ToContainTextAsync("paths");

        await page.RunAndWaitForDownloadAsync(
            () => page.ClickAsync("[data-action=\"export-diagnostics\"]"));
        var opted = await harness.ReceivedAsync<ExportDiagnosticsCommand>(command => command.IncludePaths);
        Assert.IsTrue(opted.IncludePaths);
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
        // follows the stream's state and the meter's width follows the maths.
        //
        // The denominator is the run's counted plan (TotalFiles, contract
        // 1.20), not the files seen so far, and reused files are a subset of
        // done rather than a separate tally to add on. The job's three stages
        // (ADR-0088 Amendment 1) over a 100-file plan: scanned 100, processed
        // 40, and from a service that counts nothing stored, processed
        // standing in for stored — 180 of 300, 60%. Adding the reused ten
        // would read 66. Before the plan existed the meter divided by a moving
        // denominator, so a run could show 90% and then fall back.
        harness.Clients.Client.Emit(
            new JobProgress("job-1", JobState.Packing, 100, 40, 10, 0, 1024, 512, TotalFiles: 100));

        await Expect(page.Locator(".job-live").GetByText("Packing")).ToBeVisibleAsync();
        await Expect(page.Locator(".job-live .meter > i")).ToHaveAttributeAsync("data-w", "60");
        await Expect(page.GetByText("512 B")).ToBeVisibleAsync();
    }
}
