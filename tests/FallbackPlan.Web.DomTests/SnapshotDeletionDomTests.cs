using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// Deleting a snapshot from the console, walked in a real browser (FR-GC-013,
/// ADR-0080): a snapshot row's Delete… opens a dialog that first shows the
/// dry run, which says where the snapshot is held. It then asks for the typed
/// word and the passphrase, and sends the service a sealed grant for that
/// set, never the passphrase. A deletion still waiting on a copy is marked
/// in the list with the copies it waits on.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class SnapshotDeletionDomTests
{
    private const string PassphraseText = "the console deletion passphrase";

    private static ulong NowMs => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task DeleteSnapshot_ShowsWhereItIsHeld_ThenSendsASealedGrantForItsSet()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var authority = Derive(PassphraseText, salt);
        var now = NowMs;

        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, salt, authority, now);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var row = page.Locator("#view-snapshots tr", new() { HasText = "snap-2" });
        await row.Locator("[data-action=\"delete-snapshot\"]").ClickAsync();

        // The dry run is the first thing the dialog says.
        var dialog = page.Locator("#dialog");
        await Expect(dialog).ToContainTextAsync("vault");
        var go = page.Locator("#delete-snapshot-go");
        await Expect(go).ToBeDisabledAsync();

        // The word alone does not arm it; the passphrase does.
        await page.FillAsync("#confirm-word", "delete");
        await Expect(go).ToBeDisabledAsync();
        await page.FillAsync("#delete-passphrase", PassphraseText);
        await Expect(go).ToBeEnabledAsync();
        await go.ClickAsync();

        await Expect(dialog).ToContainTextAsync("docs: snap-2 deleted (fake)");

        var sent = await harness.ReceivedAsync<DeleteSnapshotsCommand>(command => command.Apply);
        Assert.AreEqual(Wire.SetId, sent.SetId);
        Assert.AreEqual("snap-2", Assert.ContainsSingle(sent.SnapshotIds));
        CollectionAssert.AreEqual(
            authority.ReclaimKeySeed.ToArray(),
            WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(sent.ReclaimGrant!)),
            "the grant is the passphrase's own reclaim key, sealed to the service");
    }

    [TestMethod]
    public async Task Snapshots_ADeletionStillPending_SaysWhichCopiesItWaitsOn()
    {
        var now = NowMs;
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListSnapshotsCommand => new SnapshotsResult(
            [
                Wire.Snapshot(now, "snap-2") with { DeletionPending = ["usb"] },
                Wire.Snapshot(now - 120_000, "snap-1"),
            ]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#snapshots");

        var view = page.Locator("#view-snapshots");
        var pending = view.Locator("tr", new() { HasText = "snap-2" });
        await Expect(pending).ToContainTextAsync("deletion pending");
        await Expect(pending).ToContainTextAsync("usb");
        await Expect(view.Locator("tr", new() { HasText = "snap-1" })).Not.ToContainTextAsync("deletion pending");
    }

    private static RepositoryReadAuthority Derive(string passphraseText, byte[] salt)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, salt, KdfValidationMode.OpenRepository);
    }

    private static Func<ServiceCommand, ServiceResult> Service(
        byte[] recipientScalar, byte[] salt, RepositoryReadAuthority authority, ulong now)
    {
        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        var description = Wire.Describe("ready", signedInUser: "owner") with
        {
            RestoreGrantRecipient = Convert.ToHexStringLower(ContentSealing.PublicKeyOf(recipientScalar)),
            KdfSalt = Convert.ToHexStringLower(salt),
            KdfMemoryKib = parameters.MemoryKiB,
            KdfIterations = parameters.Iterations,
            KdfParallelism = parameters.Parallelism,
            SealingPublicKey = Convert.ToHexStringLower(authority.Credential.SealingPublicKey),
        };
        return command => command switch
        {
            DescribeServiceCommand => description,
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListSnapshotsCommand => new SnapshotsResult(
            [
                Wire.Snapshot(now, "snap-2"),
                Wire.Snapshot(now - 120_000, "snap-1"),
            ]),
            DeleteSnapshotsCommand { Apply: false } preview => new DeleteSnapshotsResult(
                preview.SetId, Applied: false,
                [.. preview.SnapshotIds.Select(id => new SnapshotDeletionOutcome(id, "would-delete", ["vault"]))],
                ["docs: snap-2 would be deleted from staging and vault (fake)"]),
            DeleteSnapshotsCommand apply => new DeleteSnapshotsResult(
                apply.SetId, Applied: true,
                [.. apply.SnapshotIds.Select(id => new SnapshotDeletionOutcome(id, "deleted", []))],
                ["docs: snap-2 deleted (fake)"]),
            _ => new AcknowledgedResult(),
        };
    }
}
