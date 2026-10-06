using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A running backup on the jobs page and the overview (FR-SVC-006, ADR-0088
/// and its Amendment 1). Two figures, kept apart. A bar is the job's
/// progress: three equal stages over the counted plan's files — scanned,
/// processed, and stored at every destination the run writes to — held at
/// 99% until the snapshot is published. A circle is how much of the backup a
/// destination holds: the share of the scanned files whose content is there,
/// which a run that changes nothing leaves at 100% and a first backup climbs
/// file by file. The set's own circle is its least complete destination's.
/// A destination's line says its circle in words, then what the job is doing
/// to it, and never repeats the job, which the set's live row shows in full.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class BackupProgressDomTests
{
    private const long MiB = 1024 * 1024;

    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A hundred files counted, ninety processed, forty stored.</summary>
    private static JobProgress Storing => new(
        "job-1", JobState.Packing, FilesSeen: 90, FilesDone: 90, FilesReused: 0, FilesFailed: 0,
        BytesSeen: 90 * MiB, BytesStored: 90 * MiB, TotalFiles: 100, TotalBytes: 100 * MiB,
        BytesBackedUp: 40 * MiB, FilesBackedUp: 40);

    /// <summary>Every file stored; the run is writing its source-identity hints.</summary>
    private static JobProgress Finishing => new(
        "job-1", JobState.Publishing, FilesSeen: 100, FilesDone: 100, FilesReused: 0, FilesFailed: 0,
        BytesSeen: 100 * MiB, BytesStored: 100 * MiB, TotalFiles: 100, TotalBytes: 100 * MiB,
        BytesBackedUp: 100 * MiB, HintsWritten: 347, HintsTotal: 912, FilesBackedUp: 100);

    /// <summary>Still scanning: no plan yet, so no percentage.</summary>
    private static JobProgress Scanning => new(
        "job-1", JobState.Scanning, FilesSeen: 412, FilesDone: 0, FilesReused: 0, FilesFailed: 0,
        BytesSeen: 0, BytesStored: 0);

    /// <summary>
    /// An incremental of four hundred files, half walked: of the two hundred
    /// processed, a hundred and fifty were unchanged and twenty more have
    /// been stored; thirty are changed and not stored yet.
    /// </summary>
    private static JobProgress Incremental => new(
        "job-1", JobState.Packing, FilesSeen: 200, FilesDone: 200, FilesReused: 150, FilesFailed: 0,
        BytesSeen: 20 * MiB, BytesStored: 2 * MiB, TotalFiles: 400, TotalBytes: 40 * MiB,
        BytesBackedUp: 37 * MiB, FilesBackedUp: 170);

    /// <summary>The same incremental with nothing changed so far.</summary>
    private static JobProgress Unchanged => Incremental with { FilesReused = 200, FilesBackedUp = 200 };

    private static DestinationStatusDescriptor Destination(
        string name, string kind = "local-path", long? held = 400, long? total = 400, bool whole = true,
        bool inRun = false, bool syncing = false) =>
        new(name, kind, "in-sync", NowMs, null, "independent", "verified",
            FilesHeld: held, FilesTotal: total, HoldsNewest: whole, InRun: inRun, Syncing: syncing);

    private static Func<ServiceCommand, ServiceResult> Answers(
        IReadOnlyList<DestinationStatusDescriptor> destinations,
        ProtectionState state = ProtectionState.Protected,
        bool live = true) => command => command switch
    {
        DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
        GetStatusCommand => new StatusResult(
            "vm",
            [
                new BackupSetStatusDescriptor(
                    "docs", new BackupSetStatus(state, null, []), NextRun: null, Destinations: destinations),
            ],
            NowMs,
            []),
        ListJobsCommand => new JobsResult(
            live ? [new JobDescriptor("job-1", Wire.SetId, JobState.Packing, NowMs, NowMs, null, null)] : []),
        ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
        _ => new AcknowledgedResult(),
    };

    private static async Task<IPage> OverviewAsync(DomHarness harness, IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();
        return page;
    }

    private static ILocator DestinationRow(IPage page, string name) =>
        page.Locator($"details.dest[data-dest=\"docs|{name}\"] > summary");

    [TestMethod]
    public async Task JobsMeter_IsTheJobsThreeStages_OverTheCountedPlansFiles()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers([Destination("vault", inRun: true)]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        var card = page.Locator(".job-live");

        // Scanning has no total to divide by: the stage is named and counted,
        // and no percentage is claimed.
        harness.Clients.Client.Emit(Scanning);
        await Expect(card).ToContainTextAsync("Scanning for files… 412 found");
        await Expect(card.Locator(".meter")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("indeterminate"));

        // (100 scanned + 90 processed + 40 stored) of 3 × 100.
        harness.Clients.Client.Emit(Storing);
        await Expect(card.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "76");
        await Expect(card).ToContainTextAsync("76% · scanned 100 · processed 90 · stored 40 of 100 files");
        await Expect(card).ToContainTextAsync("40.0 MiB of 100 MiB stored");
    }

    [TestMethod]
    public async Task JobsMeter_HoldsAt99_WhileTheRunFinishes_AndSaysWhatItIsDoing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers([Destination("vault", inRun: true)]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        // Every file is stored, but the snapshot is not published until the
        // finishing work lands: 99, never 100, with the step named and
        // counted beside it.
        harness.Clients.Client.Emit(Finishing);

        var card = page.Locator(".job-live");
        await Expect(card.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "99");
        await Expect(card).ToContainTextAsync("Finishing: recording file identities · 347 of 912 · 99%");
        await Expect(card).Not.ToContainTextAsync("100%");
    }

    [TestMethod]
    public async Task JobsMeter_FromAServiceWithoutTheFilesMeasure_StandsTheBytesInForTheThirdStage()
    {
        // A 1.54 service reports bytes backed up but no files: the third
        // stage is its share of the plan's bytes, so the bar still moves.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers([Destination("vault", inRun: true)]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        harness.Clients.Client.Emit(Storing with { FilesBackedUp = null });
        await Expect(page.Locator(".job-live .meter > i")).ToHaveAttributeAsync("data-w", "76");
    }

    [TestMethod]
    public async Task Overview_TheLiveRowShowsTheJobInDetail_AndTheSummaryItsBar()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers([Destination("vault", inRun: true)]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await OverviewAsync(harness, context);

        var row = page.Locator(".set-live");
        var glance = page.Locator("details.set > summary .set-live-mini");

        harness.Clients.Client.Emit(Storing);
        await Expect(row.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "76");
        await Expect(row.Locator(".detail")).ToContainTextAsync("90 processed · 40 of 100 files stored · 76%");
        await Expect(glance.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "76");
        await Expect(glance).ToContainTextAsync("76%");

        harness.Clients.Client.Emit(Finishing);
        await Expect(page.Locator("details.set > summary")).ToContainTextAsync("Finishing");
        await Expect(glance.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "99");
        await Expect(row.Locator(".detail")).ToContainTextAsync("Finishing: recording file identities · 347 of 912 · 99%");
        await Expect(row.Locator(".detail")).Not.ToContainTextAsync("100%");
    }

    [TestMethod]
    public async Task Overview_ADestinationsCircle_IsWhatItHolds_NotTheJob()
    {
        // The owner's case: the vault held the last backup whole, and the
        // run so far has found nothing changed. The vault holds every file
        // the run has scanned, so its circle says 100% while the job's bar
        // is part way — and its line says so in words, then what the job is
        // doing, without repeating the job's counts.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers([Destination("vault", inRun: true)]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await OverviewAsync(harness, context);

        harness.Clients.Client.Emit(Unchanged);
        var vault = DestinationRow(page, "vault");
        await Expect(vault.Locator(".ring-label")).ToHaveTextAsync("100%");
        await Expect(vault.Locator(".dest-caption")).ToHaveTextAsync("100% of 400 files backed up — backing up");
        await Expect(page.Locator("details.set > summary .set-ring .ring-label")).ToHaveTextAsync("100%");

        // (400 scanned + 200 processed + 200 stored) of 3 × 400.
        await Expect(page.Locator(".set-live .meter > i")).ToHaveAttributeAsync("data-w", "66");
    }

    [TestMethod]
    public async Task Overview_ACircleDipsOnlyForFilesFoundChanged_AndTheSetsIsItsLeastCompleteDestination()
    {
        // Thirty files the run found changed are not stored yet, so the
        // vault the run writes to lacks them. The offsite peer, which the
        // run does not write to, lacks the twenty stored since as well:
        // what this run adds reaches it only by its own sync, afterwards.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(
            [Destination("vault", inRun: true), Destination("offsite", kind: "peer")]);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await OverviewAsync(harness, context);

        harness.Clients.Client.Emit(Incremental);
        var vault = DestinationRow(page, "vault");
        await Expect(vault.Locator(".ring-label")).ToHaveTextAsync("92%");
        await Expect(vault.Locator(".dest-caption")).ToHaveTextAsync(
            "92% of 400 files backed up · 30 missing — backing up");

        var offsite = DestinationRow(page, "offsite");
        await Expect(offsite.Locator(".ring-label")).ToHaveTextAsync("87%");
        await Expect(offsite.Locator(".dest-caption")).ToHaveTextAsync(
            "87% of 400 files backed up · 50 missing — syncs once this backup is published");

        await Expect(page.Locator("details.set > summary .set-ring .ring-label")).ToHaveTextAsync("87%");
    }

    [TestMethod]
    public async Task Overview_OnAFirstBackup_TheCircleClimbsWithWhatIsStored_AndNeverReads100BeforeItIsPublished()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(
            [Destination("vault", held: null, total: null, whole: false, inRun: true)],
            ProtectionState.NeverBackedUp);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await OverviewAsync(harness, context);
        var vault = DestinationRow(page, "vault");

        harness.Clients.Client.Emit(Storing);
        await Expect(vault.Locator(".ring-label")).ToHaveTextAsync("40%");
        await Expect(vault.Locator(".dest-caption")).ToHaveTextAsync(
            "40% of 100 files backed up · 60 missing — backing up");

        // Every file stored, nothing published: the circle holds at 99.
        harness.Clients.Client.Emit(Finishing);
        await Expect(vault.Locator(".ring-label")).ToHaveTextAsync("99%");
        await Expect(vault.Locator(".dest-caption")).ToHaveTextAsync("99% of 100 files backed up — finishing");
        await Expect(page.Locator("details.set > summary .set-ring .ring-label")).ToHaveTextAsync("99%");
    }

    [TestMethod]
    public async Task Overview_AtRest_ACircleIsWhatEachDestinationHoldsOfTheNewestBackup()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(
            [
                Destination("vault"),
                Destination("offsite", kind: "peer", held: 370, whole: false, syncing: true),
                Destination("spare", held: null, whole: false),
            ],
            live: false);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await OverviewAsync(harness, context);

        var vault = DestinationRow(page, "vault");
        await Expect(vault.Locator(".ring-label")).ToHaveTextAsync("100%");
        await Expect(vault.Locator(".dest-caption")).ToContainTextAsync("holds all 400 files");

        // A sync under way counts as its content lands.
        var offsite = DestinationRow(page, "offsite");
        await Expect(offsite.Locator(".ring-label")).ToHaveTextAsync("92%");
        await Expect(offsite.Locator(".dest-caption")).ToHaveTextAsync(
            "92% of 400 files backed up · 30 missing — syncing");

        // Nothing ever delivered: not counted, which is not empty — and a set
        // with a destination nobody has counted has no circle of its own.
        var spare = DestinationRow(page, "spare");
        await Expect(spare.Locator(".ring-label")).ToHaveTextAsync("—");
        await Expect(spare.Locator(".dest-caption")).ToContainTextAsync("not counted yet");
        await Expect(page.Locator("details.set > summary .set-ring .ring-label")).ToHaveTextAsync("—");
    }
}
