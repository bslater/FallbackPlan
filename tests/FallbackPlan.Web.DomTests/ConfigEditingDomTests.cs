using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The configuration view's editors, walked by real clicks: the destination
/// editor, the set editor's selection tree, the typed-word delete, and the
/// write-only provisioning ceremony — each asserting the command its dialog
/// claims to send.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class ConfigEditingDomTests
{
    private static DestinationDescriptor Vault =>
        new("dest-1", "vault", "local-path", "/backups", null, null);

    [TestMethod]
    public async Task DestinationEditor_SavingALocalFolder_SendsTheUpsert()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([]),
            UpsertDestinationCommand => new ConfigurationChangeResult(["Destination 'vault' is declared."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-add-local\"]");
        await page.FillAsync("#dest-name", "vault");
        await page.FillAsync("#dest-path", "/backups");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("vault", upsert.Destination.Name);
        Assert.AreEqual("local-path", upsert.Destination.Kind);
        Assert.AreEqual("/backups", upsert.Destination.Path);

        await Expect(page.GetByText("Destination 'vault' saved.")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task SetEditor_TickingAFolder_ValidatesTheDraftAndSavesTheSet()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult([]),
            BrowseFoldersCommand { Path: null } => new FolderListingResult(
                null, null, [new FolderDescriptor("data", "/data", false, false)], []),
            BrowseFoldersCommand => new FolderListingResult("/data", null, [], []),
            ValidateSetDraftCommand => new SetDraftValidationResult([], []),
            UpsertBackupSetCommand => new ConfigurationChangeResult(["The set is saved."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        // The add button is gated on a declared destination (FR-DEST-001);
        // it enables once the fake's 'vault' arrives.
        var add = page.Locator("[data-action=\"cfg-add-set\"]");
        await Expect(add).ToBeEnabledAsync();
        await add.ClickAsync();

        // Ticking a folder in the selection tree marks it as a root, and the
        // draft round-trips through validate_set_draft (350 ms debounce).
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        var validated = await harness.ReceivedAsync<ValidateSetDraftCommand>(draft =>
            draft.Roots is { } roots && roots.Contains("/data"));
        Assert.IsNotNull(validated);

        await page.FillAsync("#set-name", "docs");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"set-save\"]");

        // A NEW set has no saved baseline, so the save is non-material and
        // goes straight to the upsert — no two-step consequence dialog.
        var upsert = await harness.ReceivedAsync<UpsertBackupSetCommand>();
        Assert.AreEqual("docs", upsert.Set.Name);
        Assert.AreEqual("/data", upsert.Set.Root);
        CollectionAssert.Contains(upsert.Set.Destinations.ToList(), "vault");

        await Expect(page.Locator("#dialog").GetByText("Backup set 'docs' saved")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task DeleteSet_TheTypedWordArmsTheButton_AndTheDeleteIsSent()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            DeleteBackupSetCommand => new ConfigurationChangeResult(["The set is removed."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-delete-set\"]");

        // The confirm-word chain: disabled until the set's own name is typed.
        var remove = page.Locator("#delete-set-go");
        await Expect(remove).ToBeDisabledAsync();
        await page.FillAsync("#confirm-word", "docs");
        await Expect(remove).ToBeEnabledAsync();
        await remove.ClickAsync();

        var deleted = await harness.ReceivedAsync<DeleteBackupSetCommand>();
        Assert.AreEqual("docs", deleted.Name);
        await Expect(page.GetByText("Backup set removed")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task WriteOnly_TheCeremonyDerivesLocally_AndSendsOnlyTheSealedEnvelope()
    {
        // A root that does not exist: the console's half of the ceremony
        // takes the CREATION path — fresh salt, a real Argon2 derivation in
        // this process — with nothing on disk (ADR-0042 §4).
        var archives = Path.Combine(Path.GetTempPath(), "fbp-none", Guid.NewGuid().ToString("n")[..12]);
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner", archivesRoot: archives),
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ProvisionWriteOnlySetCommand => new ConfigurationChangeResult(["'docs' is write-only from here on."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-write-only\"]");
        await page.FillAsync("#wo-passphrase", "Vault-Door-19-Kestrel-Harbour");
        await page.CheckAsync("#wo-ack");
        await page.ClickAsync("[data-action=\"cfg-write-only-go\"]");

        // What crosses the wire is the sealed envelope, never the passphrase
        // (NFR-SEC-009): the command carries hex sealed to the recipient key.
        var provisioned = await harness.ReceivedAsync<ProvisionWriteOnlySetCommand>();
        Assert.AreEqual("docs", provisioned.SetName);
        Assert.IsTrue(provisioned.Envelope.Length > 0, "the envelope is the ceremony's whole payload");
        Assert.DoesNotContain("Kestrel", provisioned.Envelope, StringComparison.Ordinal);

        await Expect(page.GetByText("Write-only provisioned")).ToBeVisibleAsync();
    }
}
