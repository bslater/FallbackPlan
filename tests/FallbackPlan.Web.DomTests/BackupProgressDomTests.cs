using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A running backup's percentage and meter, on the jobs page and the
/// overview alike (FR-SVC-006, ADR-0088): they show what is backed up — the
/// plan's bytes the store has acknowledged at every destination the run
/// writes to — not what has been read, and they hold at 99% while the run
/// finishes, saying what it is doing and how far through it is. 100% is
/// for a published snapshot.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class BackupProgressDomTests
{
    private const long MiB = 1024 * 1024;

    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Ninety of a hundred files read, forty of a hundred MiB stored.</summary>
    private static JobProgress Storing => new(
        "job-1", JobState.Packing, FilesSeen: 100, FilesDone: 90, FilesReused: 0, FilesFailed: 0,
        BytesSeen: 90 * MiB, BytesStored: 90 * MiB, TotalFiles: 100, TotalBytes: 100 * MiB,
        BytesBackedUp: 40 * MiB);

    /// <summary>Every byte stored; the run is writing its source-identity hints.</summary>
    private static JobProgress Finishing => new(
        "job-1", JobState.Publishing, FilesSeen: 100, FilesDone: 100, FilesReused: 0, FilesFailed: 0,
        BytesSeen: 100 * MiB, BytesStored: 100 * MiB, TotalFiles: 100, TotalBytes: 100 * MiB,
        BytesBackedUp: 100 * MiB, HintsWritten: 6_347, HintsTotal: 9_190);

    private static Func<ServiceCommand, ServiceResult> Answers(ulong now) => command => command switch
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
        ListJobsCommand => new JobsResult(
            [new JobDescriptor("job-1", Wire.SetId, JobState.Publishing, now, now, null, null)]),
        ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
        _ => new AcknowledgedResult(),
    };

    [TestMethod]
    public async Task JobsMeter_FollowsWhatIsBackedUp_NotWhatIsRead()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(NowMs);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        // Ninety files read is not ninety per cent backed up: forty MiB of
        // the hundred are at the destination, and that is the meter.
        harness.Clients.Client.Emit(Storing);

        var card = page.Locator(".job-live");
        await Expect(card.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "40");
        await Expect(card).ToContainTextAsync("40.0 MiB of 100 MiB backed up");
        await Expect(card).ToContainTextAsync("40%");
    }

    [TestMethod]
    public async Task JobsMeter_HoldsAt99_WhileTheRunFinishes_AndSaysWhatItIsDoing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(NowMs);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");
        await Expect(page.GetByText("Waiting for the first progress event")).ToBeVisibleAsync();

        // Every byte is stored, but the snapshot is not published until the
        // finishing work lands: 99, never 100, with the step named and
        // counted beside it.
        harness.Clients.Client.Emit(Finishing);

        var card = page.Locator(".job-live");
        await Expect(card.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "99");
        await Expect(card).ToContainTextAsync("Finishing");
        await Expect(card).ToContainTextAsync("6,347 of 9,190");
        await Expect(card).ToContainTextAsync("99%");
        await Expect(card).Not.ToContainTextAsync("100%");
    }

    [TestMethod]
    public async Task Overview_TheSetsLiveRow_ShowsWhatIsBackedUp_AndHoldsAt99WhileFinishing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(NowMs);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();

        var row = page.Locator(".set-live");
        await Expect(row).ToBeVisibleAsync();

        harness.Clients.Client.Emit(Storing);
        await Expect(row.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "40");
        await Expect(row.Locator(".detail")).ToContainTextAsync("40.0 MiB of 100 MiB backed up · 40%");

        harness.Clients.Client.Emit(Finishing);
        await Expect(row.Locator(".meter > i")).ToHaveAttributeAsync("data-w", "99");
        await Expect(row.Locator(".detail")).ToContainTextAsync("Finishing");
        await Expect(row.Locator(".detail")).ToContainTextAsync("6,347 of 9,190");
        await Expect(row.Locator(".detail")).ToContainTextAsync("99%");
        await Expect(row.Locator(".detail")).Not.ToContainTextAsync("100%");
    }
}
