using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A set's card on the overview says which online-only files its last backup
/// could not download from their provider (FR-SNP-014, ADR-0193): how many at
/// a glance on the card itself, and when it is opened, how many keep the
/// version backed up before and how many are not in that backup, since when,
/// and on hover the file whose download failed and the provider's words,
/// and that the set may leave online-only files to their provider instead
/// (ADR-0193 Amendment 2). A set whose last backup downloaded every one says
/// nothing.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class NotFetchedDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task Overview_ASetsCard_SaysHowManyFilesItsLastBackupDidNotDownload()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            Set("docs", new NotFetchedDescriptor(3, 2, NowMs - 3 * 3_600_000, "docs/a.docx", "The cloud operation timed out.")));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var docs = page.Locator("details.set[data-set=\"docs\"]");
        await Expect(docs.Locator("summary")).ToContainTextAsync("3 not downloaded");
        await docs.Locator("summary").ClickAsync();
        var line = docs.Locator(".set-body [data-not-fetched]");
        await Expect(line).ToContainTextAsync("3 online-only files were not downloaded by the last backup");
        await Expect(line).ToContainTextAsync("2 keep the version backed up before, 1 is not in it at all");
        await Expect(line).ToContainTextAsync("first missed 3 h ago");
        await Expect(line).ToHaveAttributeAsync(
            "title", "docs/a.docx could not be downloaded: The cloud operation timed out. No other online-only file was asked for after it. "
                + LeaveHint);
    }

    // What a person may do instead of downloading them, said with its cost.
    private const string LeaveHint =
        "The set's Other settings can leave online-only files to their cloud provider instead: "
        + "none is downloaded, and one new or changed there is kept only by its provider.";

    [TestMethod]
    public async Task Overview_EachKeptOrNoneKept_SaysWhichInWords()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            Set("letters", new NotFetchedDescriptor(1, 1, NowMs, "letters/x.docx", "the provider is offline")),
            Set("photos", new NotFetchedDescriptor(2, 0, NowMs, "photos/new.jpg", "offline")));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var letters = page.Locator("details.set[data-set=\"letters\"]");
        await letters.Locator("summary").ClickAsync();
        var kept = letters.Locator(".set-body [data-not-fetched]");
        await Expect(kept).ToContainTextAsync(
            "1 online-only file was not downloaded by the last backup; it keeps the version backed up before");
        await Expect(kept).ToHaveAttributeAsync(
            "title", "letters/x.docx could not be downloaded: the provider is offline. No other online-only file was asked for after it. "
                + LeaveHint);

        var photos = page.Locator("details.set[data-set=\"photos\"]");
        await photos.Locator("summary").ClickAsync();
        await Expect(photos.Locator(".set-body [data-not-fetched]")).ToContainTextAsync(
            "2 online-only files were not downloaded by the last backup; none of them is in that backup");
    }

    [TestMethod]
    public async Task Overview_ASetWhoseLastBackupDownloadedEveryOne_SaysNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(Set("music", notFetched: null));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var music = page.Locator("details.set[data-set=\"music\"]");
        await Expect(music.Locator("summary")).Not.ToContainTextAsync("not downloaded");
        await music.Locator("summary").ClickAsync();
        await Expect(music.Locator(".set-body")).ToContainTextAsync("manual only");
        await Expect(music.Locator(".set-body [data-not-fetched]")).ToHaveCountAsync(0);
    }

    private static BackupSetStatusDescriptor Set(string name, NotFetchedDescriptor? notFetched) =>
        new(name, new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
            Destinations: [], NotFetched: notFetched);

    private static Func<ServiceCommand, ServiceResult> Service(params BackupSetStatusDescriptor[] sets) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult("vm", sets, NowMs, []),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };
}
