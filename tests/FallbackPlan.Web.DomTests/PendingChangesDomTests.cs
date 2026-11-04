using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A set's card on the overview says how many changed files its next backup
/// will take (FR-SNP-013, ADR-0192): at a glance on the card itself where any
/// are waiting, and in words beside the next run when it is opened. A card
/// whose next backup will walk in full says so, and why on hover; one with
/// nothing changed says that; and a set whose backups ask no change journal
/// says nothing. Where some must be downloaded from their cloud provider
/// first, it says how many and how much (ADR-0192 Amendment 2).
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class PendingChangesDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task Overview_ASetsCard_SaysHowManyChangedFilesItsNextBackupWillTake()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            Set("docs", new PendingChangesDescriptor(12, null, NowMs)),
            Set("letters", new PendingChangesDescriptor(1, null, NowMs)));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var docs = page.Locator("details.set[data-set=\"docs\"]");
        await Expect(docs.Locator("summary")).ToContainTextAsync("12 changed");
        await docs.Locator("summary").ClickAsync();
        await Expect(docs.Locator(".set-body")).ToContainTextAsync("12 changed files will be backed up next");

        var letters = page.Locator("details.set[data-set=\"letters\"]");
        await letters.Locator("summary").ClickAsync();
        await Expect(letters.Locator(".set-body")).ToContainTextAsync("1 changed file will be backed up next");
    }

    [TestMethod]
    public async Task Overview_ChangedFilesTheNextBackupMustDownload_AreSaidWithTheirSize()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            Set("docs", new PendingChangesDescriptor(12, null, NowMs, FilesToFetch: 3, BytesToFetch: 2 * 1024 * 1024)),
            Set("letters", new PendingChangesDescriptor(4, null, NowMs, FilesToFetch: 0, BytesToFetch: 0)),
            Set("music", new PendingChangesDescriptor(4, null, NowMs, FilesToFetch: 4, BytesToFetch: 4096)),
            Set("notes", new PendingChangesDescriptor(1, null, NowMs, FilesToFetch: 1, BytesToFetch: 2048)));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var docs = page.Locator("details.set[data-set=\"docs\"]");
        await docs.Locator("summary").ClickAsync();
        await Expect(docs.Locator(".set-body [data-pending]")).ToContainTextAsync(
            "12 changed files will be backed up next; 3 of them, 2.0 MiB, must be downloaded from the cloud first");

        var letters = page.Locator("details.set[data-set=\"letters\"]");
        await letters.Locator("summary").ClickAsync();
        await Expect(letters.Locator(".set-body [data-pending]")).ToHaveTextAsync("4 changed files will be backed up next");

        var music = page.Locator("details.set[data-set=\"music\"]");
        await music.Locator("summary").ClickAsync();
        await Expect(music.Locator(".set-body [data-pending]")).ToHaveTextAsync(
            "4 changed files will be backed up next; all of them, 4.0 KiB, must be downloaded from the cloud first");

        var notes = page.Locator("details.set[data-set=\"notes\"]");
        await notes.Locator("summary").ClickAsync();
        await Expect(notes.Locator(".set-body [data-pending]")).ToHaveTextAsync(
            "1 changed file will be backed up next; it, 2.0 KiB, must be downloaded from the cloud first");
    }

    [TestMethod]
    public async Task Overview_ASetWhoseNextBackupWalksInFull_SaysSoAndWhy()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(Set("photos", new PendingChangesDescriptor(null, "its full walk is due", NowMs)));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var photos = page.Locator("details.set[data-set=\"photos\"]");
        await Expect(photos.Locator("summary")).Not.ToContainTextAsync("changed");
        await photos.Locator("summary").ClickAsync();
        var clause = photos.Locator(".set-body [data-pending]");
        await Expect(clause).ToHaveTextAsync("next backup walks in full");
        await Expect(clause).ToHaveAttributeAsync("title", "The next backup walks in full: its full walk is due.");
    }

    [TestMethod]
    public async Task Overview_ASetWithNothingChanged_SaysSo_AndOneThatAsksNoJournalSaysNothing()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(
            Set("music", new PendingChangesDescriptor(0, null, NowMs)),
            Set("books", pending: null));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        var music = page.Locator("details.set[data-set=\"music\"]");
        await Expect(music.Locator("summary")).Not.ToContainTextAsync("changed");
        await music.Locator("summary").ClickAsync();
        await Expect(music.Locator(".set-body [data-pending]")).ToHaveTextAsync("nothing changed since the last backup");

        var books = page.Locator("details.set[data-set=\"books\"]");
        await books.Locator("summary").ClickAsync();
        await Expect(books.Locator(".set-body")).ToContainTextAsync("manual only");
        await Expect(books.Locator(".set-body [data-pending]")).ToHaveCountAsync(0);
    }

    private static BackupSetStatusDescriptor Set(string name, PendingChangesDescriptor? pending) =>
        new(name, new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
            Destinations: [], PendingChanges: pending);

    private static Func<ServiceCommand, ServiceResult> Service(params BackupSetStatusDescriptor[] sets) =>
        command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult("vm", sets, NowMs, []),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };
}
