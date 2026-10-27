using FallbackPlan.Api;
using FallbackPlan.Domain.Status;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// A cloud destination whose store holds the replica's metadata only in an
/// archival tier, on its card on the overview (FR-DEST-020, contract 1.70):
/// a badge on the card's summary, seen without opening it, because the copy
/// is in sync and the circle full while no restore can open it; and in the
/// card, the object to rehydrate and what a lifecycle rule must leave served.
/// A destination that names no such object draws neither.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class ArchivedMetadataDomTests
{
    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task ACloudDestinationWhoseStoreArchivedTheReplicasMetadata_SaysNothingRestoresFromIt()
    {
        await using var harness = await DomHarness.StartAsync();
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
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true),
                            new DestinationStatusDescriptor(
                                "cloud", "s3", "in-sync", NowMs, null, "offsite", "verified",
                                FilesHeld: 400, FilesTotal: 400, HoldsNewest: true,
                                ArchivedMetadata: "repository-format", ArchivedMetadataSince: NowMs - 86_400_000),
                        ]),
                ],
                NowMs,
                []),
            ListJobsCommand => new JobsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);
        await Expect(page.Locator("#view-overview").GetByText("docs")).ToBeVisibleAsync();

        var cloud = page.Locator("details.dest[data-dest=\"docs|cloud\"]");
        await Expect(cloud.Locator("> summary").GetByText("restores nothing")).ToBeVisibleAsync();
        await cloud.Locator("> summary").ClickAsync();
        await Expect(cloud.GetByText("repository-format", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(cloud.GetByText("blobs/data/", new() { Exact = false })).ToBeVisibleAsync();

        var vault = page.Locator("details.dest[data-dest=\"docs|vault\"]");
        await Expect(vault.Locator("> summary").GetByText("restores nothing")).ToHaveCountAsync(0);
    }
}
