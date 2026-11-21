using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// Where a destination's proof of new backups stands, on its card on the
/// overview (FR-VER-003, contract 1.91, ADR-0199 Amendment 2): the backup it
/// was last proven through, and a proof under way said as progress, how many
/// of the backup's blobs are proven of how many it needs. A destination the
/// service reports no proof for draws no row, never one proven through
/// nothing.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class NewBackupProofDomTests
{
    private const string Earlier = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string Newest = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";

    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task AProofUnderWay_IsDrawnAsProgress_BesideTheBackupProvenBeforeIt()
    {
        await using var harness = await StartAsync(new NewBackupProofDescriptor(
            Earlier, NowMs - 86_400_000, ProvingSnapshotId: Newest, ProvingCapturedAt: NowMs - 3_600_000,
            BlobsProven: 3, BlobsLeft: 7));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenAsync(context, harness);

        var vault = page.Locator("details.dest[data-dest=\"docs|vault\"]");
        await vault.Locator("> summary").ClickAsync();
        await Expect(vault.GetByText("Proven backup", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(vault.GetByText("checking the backup of", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(vault.GetByText("3 of 10 blobs proven", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(vault.GetByText("proven before it", new() { Exact = false })).ToBeVisibleAsync();

        var cloud = page.Locator("details.dest[data-dest=\"docs|cloud\"]");
        await cloud.Locator("> summary").ClickAsync();
        await Expect(cloud.GetByText("Failure domain")).ToBeVisibleAsync();
        await Expect(cloud.GetByText("Proven backup", new() { Exact = true })).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task ADestinationProvenThroughABackup_SaysWhich_AndThatNothingIsBeingChecked()
    {
        await using var harness = await StartAsync(new NewBackupProofDescriptor(Earlier, NowMs - 86_400_000));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenAsync(context, harness);

        var vault = page.Locator("details.dest[data-dest=\"docs|vault\"]");
        await vault.Locator("> summary").ClickAsync();
        await Expect(vault.GetByText("Proven backup", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(vault.GetByText("the backup of", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(vault.GetByText("checking", new() { Exact = false })).ToHaveCountAsync(0);
    }

    [TestMethod]
    public async Task AFirstProofUnderWay_SaysNoBackupIsProvenThereYet()
    {
        await using var harness = await StartAsync(new NewBackupProofDescriptor(
            null, null, ProvingSnapshotId: Newest, ProvingCapturedAt: NowMs - 3_600_000, BlobsProven: 0, BlobsLeft: 12));
        await using var context = await BrowserSession.NewContextAsync();
        var page = await OpenAsync(context, harness);

        var vault = page.Locator("details.dest[data-dest=\"docs|vault\"]");
        await vault.Locator("> summary").ClickAsync();
        await Expect(vault.GetByText("0 of 12 blobs proven", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(vault.GetByText("no backup proven here yet", new() { Exact = false })).ToBeVisibleAsync();
    }

    private static async Task<DomHarness> StartAsync(NewBackupProofDescriptor proof)
    {
        var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetStatusCommand => new StatusResult(
                "vm",
                [
                    new BackupSetStatusDescriptor(
                        "docs", new BackupSetStatus(ProtectionState.Protected, null, []), NextRun: null,
                        Destinations:
                        [
                            new DestinationStatusDescriptor(
                                "vault", "local-path", "in-sync", NowMs, null, "independent", "verified",
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true, NewBackupProof: proof),
                            new DestinationStatusDescriptor(
                                "cloud", "s3", "in-sync", NowMs, null, "offsite", "verified",
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true),
                        ]),
                ],
                NowMs,
                []),
            ListJobsCommand => new JobsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };

        return harness;
    }

    private static async Task<Microsoft.Playwright.IPage> OpenAsync(
        Microsoft.Playwright.IBrowserContext context, DomHarness harness)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();
        return page;
    }
}
