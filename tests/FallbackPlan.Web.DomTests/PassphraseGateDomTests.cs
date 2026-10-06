using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The passphrase gate as a person meets it (FR-WOR-007, ADR-0089): every
/// action that names a backup's files — browsing a snapshot, a run's changes
/// and failures, a set's "what changed" — asks for the passphrase first and
/// asks again the next time, and reads through the restore source the
/// passphrase unlocked, closing it after. A wrong passphrase opens nothing
/// and names nothing.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class PassphraseGateDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Func<ServiceCommand, ServiceResult> Answers(ulong now) => command => command switch
    {
        DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
        GetStatusCommand => new StatusResult(
            "vm",
            [new BackupSetStatusDescriptor("docs", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null, Destinations: [])],
            now,
            []),
        ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
        ListSnapshotsCommand => new SnapshotsResult([Wire.Snapshot(now)]),
        ListJobsCommand => new JobsResult(
            [new JobDescriptor("job-1", Wire.SetId, JobState.Complete, now, now, "snap-1", null)]),
        OpenRestoreSourceCommand => new RestoreSourceOpenedResult("src-1", "docs", "staging", [Wire.Snapshot(now)], []),
        ListDirectoryCommand => new DirectoryResult(
            "", [new DirectoryEntryDescriptor("notes.txt", "file", 42, now, "same")]),
        JobChangesCommand => new JobChangesResult(
            "docs", "snap-1", null, null, 0,
            New: new ChangeBucketDescriptor(1, ["fresh.txt"]),
            Changed: new ChangeBucketDescriptor(0, []),
            Removed: new ChangeBucketDescriptor(0, []),
            SampleLimit: 20),
        JobFailuresCommand => new JobFailuresResult(
            "docs", "snap-1", 1, [new CaptureFailureDescriptor("locked.db", "permission", "Access denied.")], 100),
        PreviewSetChangesCommand => new SetChangePreviewResult(
            "docs", "snap-1", now, 3,
            New: new ChangeBucketDescriptor(0, []),
            Updated: new ChangeBucketDescriptor(0, []),
            MetadataOnly: new ChangeBucketDescriptor(0, []),
            Moved: new ChangeBucketDescriptor(0, []),
            Deleted: new ChangeBucketDescriptor(1, ["gone.txt"]),
            NoLongerIncluded: new ChangeBucketDescriptor(0, []),
            Failures: 0, SampleLimit: 20),
        _ => new AcknowledgedResult(),
    };

    private static async Task UnlockAsync(IPage page, string passphrase)
    {
        await Expect(page.Locator("#gate-passphrase")).ToBeVisibleAsync();
        await page.FillAsync("#gate-passphrase", passphrase);
        await page.ClickAsync("[data-action=\"gate-unlock\"]");
    }

    private static int Count<TCommand>(DomHarness harness)
        where TCommand : ServiceCommand
    {
        lock (harness.Clients.Client.Received)
        {
            return harness.Clients.Client.Received.OfType<TCommand>().Count();
        }
    }

    [TestMethod]
    public async Task Browse_AsksForThePassphraseEveryTime_AndListsOnlyThroughWhatItUnlocked()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(now);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");
        await page.ClickAsync("[data-action=\"browse\"]");

        // A wrong passphrase opens nothing and names nothing.
        await UnlockAsync(page, "not the passphrase");
        await Expect(page.Locator("#dialog")).ToContainTextAsync("does not open");
        await Expect(page.Locator("#dialog")).Not.ToContainTextAsync("notes.txt");
        Assert.AreEqual(0, Count<OpenRestoreSourceCommand>(harness), "a wrong passphrase sends the service nothing");
        Assert.AreEqual(0, Count<ListDirectoryCommand>(harness));

        await UnlockAsync(page, Wire.Passphrase);
        await Expect(page.Locator("#dialog").GetByText("notes.txt")).ToBeVisibleAsync();

        var opened = await harness.ReceivedAsync<OpenRestoreSourceCommand>();
        Assert.AreEqual("docs", opened.SetName);
        Assert.IsNull(opened.DestinationName);
        Assert.IsTrue(Wire.IsTheInstallationsGrant(opened.Envelope), "the source opens under the set's grant");
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<ListDirectoryCommand>()).Source);

        // Closing the listing closes what the passphrase unlocked, and the
        // next look asks again.
        await page.ClickAsync("#dialog [data-action=\"close-dialog\"]");
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<CloseRestoreSourceCommand>()).SourceId);

        await page.ClickAsync("[data-action=\"browse\"]");
        await Expect(page.Locator("#gate-passphrase")).ToBeVisibleAsync();
        await Expect(page.Locator("#dialog")).Not.ToContainTextAsync("notes.txt");
    }

    [TestMethod]
    public async Task ARunsChangesAndFailures_AreReadThroughASourceUnlockedForEachLook()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(now);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");

        await page.ClickAsync("[data-action-row=\"job-details\"]");
        await page.ClickAsync("[data-action=\"job-changes\"]");
        await UnlockAsync(page, Wire.Passphrase);
        await Expect(page.Locator("#dialog").GetByText("fresh.txt")).ToBeVisibleAsync();
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<JobChangesCommand>()).Source);
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<CloseRestoreSourceCommand>()).SourceId,
            "the source is closed once the report is drawn");

        await page.ClickAsync("#dialog [data-action=\"close-dialog\"]");
        await page.ClickAsync("[data-action-row=\"job-details\"]");
        await page.ClickAsync("[data-action=\"job-failures\"]");

        // Asked again: one proof serves one look.
        await UnlockAsync(page, Wire.Passphrase);
        await Expect(page.Locator("#dialog").GetByText("locked.db")).ToBeVisibleAsync();
        Assert.AreEqual("src-1", (await harness.ReceivedAsync<JobFailuresCommand>()).Source);
        Assert.AreEqual(2, Count<OpenRestoreSourceCommand>(harness), "each look unlocked its own source");
    }

    [TestMethod]
    public async Task ASetsWhatChanged_AsksForThePassphrase_AndNamesWhatOnlyTheBackupHolds()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Answers(now);

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await page.ClickAsync("details.set[data-set=\"docs\"] > summary");
        await page.ClickAsync("[data-action=\"what-changed\"]");

        await UnlockAsync(page, Wire.Passphrase);
        await Expect(page.Locator("#dialog").GetByText("gone.txt")).ToBeVisibleAsync();
        var asked = await harness.ReceivedAsync<PreviewSetChangesCommand>();
        Assert.AreEqual("docs", asked.SetName);
        Assert.AreEqual("src-1", asked.Source);
    }
}
