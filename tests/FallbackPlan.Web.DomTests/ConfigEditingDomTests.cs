using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The configuration view's editors, walked by real clicks: the destination
/// editor, the set editor's selection tree, the typed-word delete, the
/// acknowledgement of a claim held on a replica stored here (FR-DR-005), the
/// write-only provisioning ceremony, the adoption ceremony's preview and
/// confirmation (FR-DR-009), the service-settings card with the
/// destination form's limit and cadence (FR-SVC-021), an S3-compatible
/// store's address and access key (FR-DEST-005), and an Azure Blob
/// container's address with either of its credentials (ADR-0093) — each
/// asserting the command its dialog claims to send.
/// </summary>
/// <remarks>
/// Re-homed onto the sectioned set editor when this line merged: the single
/// form this suite filled in one pass became a summary with a Change… dialog
/// per section and one confirm at the end. What is asserted — the tree's tick
/// becomes a root, the draft is validated as it is built, and the editor sends
/// exactly one upsert carrying the name, root and destination — is unchanged.
/// Re-homed again in 2026-10, for a new set only: making a set became the
/// stepped wizard (FR-SVC-022, <c>NewSetWizardDomTests</c>), so the walk below
/// answers its three required steps and creates from the third. Editing a set
/// keeps the summary.
/// </remarks>
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
    public async Task DestinationEditor_AnS3Store_SendsItsAddress_ThenItsKeySealedInTheConsole()
    {
        // The address rides the upsert like any destination's; the secret
        // goes to the console's own endpoint, which seals it to the service
        // for this destination and key id, so the relay is never sent it
        // (FR-DEST-005, ADR-0091).
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([]),
            UpsertDestinationCommand => new AcknowledgedResult(),
            SetDestinationCredentialsCommand => new ConfigurationChangeResult(
                ["Access key FBPKEYID0001 stored for destination 'cloud'."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-add-s3\"]");
        await page.FillAsync("#dest-name", "cloud");
        await page.FillAsync("#dest-endpoint", "https://objects.example.net");
        await page.FillAsync("#dest-bucket", "family-backups");
        await page.FillAsync("#dest-region", "eu-test-1");
        await page.FillAsync("#dest-prefix", "site-a");
        await page.SelectOptionAsync("#dest-addressing", "virtual-host");
        await page.FillAsync("#dest-key-id", "FBPKEYID0001");
        await page.FillAsync("#dest-secret", "fbp/dom+secret=key/0123456789abcdef");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("s3", upsert.Destination.Kind);
        Assert.AreEqual("https://objects.example.net", upsert.Destination.Endpoint);
        Assert.AreEqual("family-backups", upsert.Destination.Bucket);
        Assert.AreEqual("eu-test-1", upsert.Destination.Region);
        Assert.AreEqual("site-a", upsert.Destination.Prefix);
        Assert.AreEqual("virtual-host", upsert.Destination.Addressing);
        Assert.IsNull(upsert.Destination.Path);
        Assert.IsNull(upsert.Destination.Fingerprint);

        var stored = await harness.ReceivedAsync<SetDestinationCredentialsCommand>();
        Assert.AreEqual("cloud", stored.DestinationName);
        Assert.AreEqual("FBPKEYID0001", stored.AccessKeyId);
        Assert.AreEqual(
            "fbp/dom+secret=key/0123456789abcdef",
            WriteOnlyProvisioning.OpenAccessKeySecret(
                Wire.RecipientScalar, Convert.FromHexString(stored.Envelope), "cloud", "FBPKEYID0001"));

        await Expect(page.GetByText("Access key FBPKEYID0001 stored for destination 'cloud'.")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task DestinationEditor_AnS3Store_TakesADeepVerifyCadence_AndSaysAnEmptyOneMeansNever()
    {
        // A store is swept only on a cadence its operator states, because
        // every read there is a request its provider may charge for (ADR-0091
        // Amendment 1): the field is offered, and empty means never.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([]),
            UpsertDestinationCommand => new AcknowledgedResult(),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-add-s3\"]");
        await Expect(page.Locator("#dest-sweep")).ToHaveAttributeAsync("placeholder", "never");
        await page.FillAsync("#dest-name", "cloud");
        await page.FillAsync("#dest-endpoint", "https://objects.example.net");
        await page.FillAsync("#dest-bucket", "family-backups");
        await page.FillAsync("#dest-sweep", "30");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("s3", upsert.Destination.Kind);
        Assert.AreEqual(30, upsert.Destination.DeepVerifyIntervalDays);
    }

    [TestMethod]
    public async Task DestinationsTable_AnS3Store_OffersToFindTheBackupsItHolds()
    {
        // The recovery path on a fresh machine (ADR-0091 Amendment 1):
        // declare the bucket, store its key, find what it holds, adopt.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult(
            [
                new DestinationDescriptor(
                    "dest-s3", "cloud", "s3", null, null, "https://objects.example.net",
                    Bucket: "family-backups", AccessKeyStored: true),
            ]),
            DiscoverArchivesCommand => new ArchivesDiscoveredResult("cloud", [], []),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-discover\"][data-name=\"cloud\"]");

        var discover = await harness.ReceivedAsync<DiscoverArchivesCommand>();
        Assert.AreEqual("cloud", discover.DestinationName);
        await Expect(page.GetByText("Backups at 'cloud'")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task DestinationEditor_EditingAnS3Store_ShowsWhetherAKeyIsHeld_AndSendsNoneUntilOneIsTyped()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult(
            [
                new DestinationDescriptor(
                    "dest-s3", "cloud", "s3", null, null, "https://objects.example.net",
                    Bucket: "family-backups", AccessKeyStored: true),
            ]),
            UpsertDestinationCommand => new AcknowledgedResult(),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-edit-dest\"][data-id=\"dest-s3\"]");
        await Expect(page.Locator("#dest-bucket")).ToHaveValueAsync("family-backups");
        await Expect(page.Locator("#dest-key-held")).ToContainTextAsync("An access key is held");
        await Expect(page.Locator("#dest-secret")).ToHaveValueAsync("");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("dest-s3", upsert.Destination.Id);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<SetDestinationCredentialsCommand>(),
            "a key nobody typed is not sent, and the one held stays");
    }

    [TestMethod]
    public async Task DestinationEditor_AnAzureBlobContainer_SendsItsAddress_ThenItsAccountKeySealedInTheConsole()
    {
        // An account and a container, an optional endpoint, and the account
        // key — sealed by the console's own endpoint for this destination, so
        // the relay is never sent it (FR-DEST-005, ADR-0093).
        const string accountKey = "BwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4/QEFCQ0RFRg==";
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([]),
            UpsertDestinationCommand => new AcknowledgedResult(),
            SetDestinationCredentialsCommand => new ConfigurationChangeResult(
                ["Account key stored for destination 'cloud'."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-add-azure-blob\"]");
        await Expect(page.Locator("#dest-sweep")).ToHaveAttributeAsync("placeholder", "never");
        await page.FillAsync("#dest-name", "cloud");
        await page.FillAsync("#dest-account", "fbptestaccount");
        await page.FillAsync("#dest-container", "family-backups");
        await page.FillAsync("#dest-prefix", "site-a");
        await page.SelectOptionAsync("#dest-credential-kind", "shared-key");
        await page.FillAsync("#dest-secret", accountKey);
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("azure-blob", upsert.Destination.Kind);
        Assert.AreEqual("fbptestaccount", upsert.Destination.Account);
        Assert.AreEqual("family-backups", upsert.Destination.Container);
        Assert.AreEqual("site-a", upsert.Destination.Prefix);
        Assert.IsNull(upsert.Destination.Endpoint, "an empty endpoint is the account at the public service");
        Assert.IsNull(upsert.Destination.Bucket);
        Assert.IsNull(upsert.Destination.Region);

        var stored = await harness.ReceivedAsync<SetDestinationCredentialsCommand>();
        Assert.AreEqual("cloud", stored.DestinationName);
        Assert.AreEqual("shared-key", stored.CredentialKind);
        Assert.IsNull(stored.AccessKeyId);
        Assert.AreEqual(
            accountKey, WriteOnlyProvisioning.OpenAccountKey(Wire.RecipientScalar, Convert.FromHexString(stored.Envelope), "cloud"));

        await Expect(page.GetByText("Account key stored for destination 'cloud'.")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task DestinationEditor_EditingAnAzureBlobContainer_SaysWhichCredentialIsHeld_AndTakesASignature()
    {
        const string token = "sv=2024-11-04&sr=c&sp=racwdl&se=2099-12-31T00%3A00%3A00Z&sig=AbC%2Bd%2Fe%3D";
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult(
            [
                new DestinationDescriptor(
                    "dest-azure", "cloud", "azure-blob", null, null, null, Prefix: "site-a", AccessKeyStored: true,
                    Account: "fbptestaccount", Container: "family-backups",
                    CredentialKind: "sas", CredentialExpires: "2026-12-31T00:00:00Z"),
            ]),
            UpsertDestinationCommand => new AcknowledgedResult(),
            SetDestinationCredentialsCommand => new ConfigurationChangeResult(["Shared access signature stored."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-edit-dest\"][data-id=\"dest-azure\"]");
        await Expect(page.Locator("#dest-account")).ToHaveValueAsync("fbptestaccount");
        await Expect(page.Locator("#dest-container")).ToHaveValueAsync("family-backups");
        await Expect(page.Locator("#dest-key-held")).ToContainTextAsync("A shared access signature is held");
        await Expect(page.Locator("#dest-key-held")).ToContainTextAsync("2026-12-31");
        await Expect(page.Locator("#dest-secret")).ToHaveValueAsync("");

        await page.SelectOptionAsync("#dest-credential-kind", "sas");
        await page.FillAsync("#dest-secret", token);
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("dest-azure", upsert.Destination.Id);
        var stored = await harness.ReceivedAsync<SetDestinationCredentialsCommand>();
        Assert.AreEqual("sas", stored.CredentialKind);
        Assert.AreEqual(
            token, WriteOnlyProvisioning.OpenSharedAccessSignature(Wire.RecipientScalar, Convert.FromHexString(stored.Envelope), "cloud"));
    }

    [TestMethod]
    public async Task DestinationsTable_AnAzureBlobContainer_OffersToFindItsBackups_AndSaysWhenItsSignatureLapsed()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult(
            [
                new DestinationDescriptor(
                    "dest-azure", "cloud", "azure-blob", null, null, null, Prefix: "site-a", AccessKeyStored: true,
                    Account: "fbptestaccount", Container: "family-backups",
                    CredentialKind: "sas", CredentialExpires: "2020-01-01T00:00:00Z"),
            ]),
            DiscoverArchivesCommand => new ArchivesDiscoveredResult("cloud", [], []),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await Expect(page.GetByText("fbptestaccount · family-backups/site-a")).ToBeVisibleAsync();
        await Expect(page.GetByText("signature expired")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"dest-discover\"][data-name=\"cloud\"]");

        var discover = await harness.ReceivedAsync<DiscoverArchivesCommand>();
        Assert.AreEqual("cloud", discover.DestinationName);
    }

    [TestMethod]
    public async Task DestinationEditor_ALimitAndACadence_RideTheUpsert()
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
        await page.FillAsync("#dest-limit", "2 MiB/s");
        await page.FillAsync("#dest-drill", "7");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("2 MiB/s", upsert.Destination.TransferLimit);
        Assert.AreEqual(7, upsert.Destination.DrillIntervalDays);
    }

    [TestMethod]
    public async Task DestinationEditor_EmptyingTheSettings_SendsTheSpellingsThatClearThem()
    {
        // The form shows what the destination holds and sends what the form
        // holds: an emptied limit and cadence must clear, not keep.
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult(
                [Vault with { TransferLimit = "2 MiB/s", DrillIntervalDays = 3 }]),
            UpsertDestinationCommand => new ConfigurationChangeResult(["Destination 'vault' is declared."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"cfg-edit-dest\"][data-id=\"dest-1\"]");
        await Expect(page.Locator("#dest-limit")).ToHaveValueAsync("2 MiB/s");
        await Expect(page.Locator("#dest-drill")).ToHaveValueAsync("3");
        await page.FillAsync("#dest-limit", "");
        await page.FillAsync("#dest-drill", "");
        await page.ClickAsync("[data-action=\"dest-save\"]");

        var upsert = await harness.ReceivedAsync<UpsertDestinationCommand>();
        Assert.AreEqual("", upsert.Destination.TransferLimit);
        Assert.AreEqual(0, upsert.Destination.DrillIntervalDays);
    }

    [TestMethod]
    public async Task DestinationEditor_ASaveTheServiceSaysSomethingAbout_ShowsWhatItSaid()
    {
        // The service answers a destination save with lines only when it has
        // something to say back: a relative path it resolved, or a move onto
        // a root's drive that only a Debug build allows (ADR-0051 Amendment
        // 2). A toast that says only "saved" drops them.
        const string Said =
            "Moving 'vault' to '/srv/vault' puts it on the same volume as root '/home/me' of backup set 'docs', "
            + "which only a Debug build allows: a Release build refuses it, because a backup on the drive the files "
            + "live on dies with them (ADR-0051).";
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            ListDestinationsCommand => new DestinationsResult([]),
            UpsertDestinationCommand => new ConfigurationChangeResult([Said]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-add-local\"]");
        await page.FillAsync("#dest-name", "vault");
        await page.FillAsync("#dest-path", "/srv/vault");
        await page.ClickAsync("[data-action=\"dest-save\"]");
        await harness.ReceivedAsync<UpsertDestinationCommand>();

        await Expect(page.Locator("#dialog h3")).ToHaveTextAsync("Destination 'vault' saved");
        await Expect(page.Locator("#dialog .report")).ToHaveTextAsync(Said);
    }

    [TestMethod]
    public async Task ServiceSettingsCard_Saving_SendsTheUpdateAndShowsWhatTheServiceSaid()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetServiceSettingsCommand => new ServiceSettingsResult(null, null, null, EffectiveMaxConcurrentBackups: 2),
            UpdateServiceSettingsCommand => new ConfigurationChangeResult(
                ["Background window set to 22:00-06:00; it applies from the next pass."]),
            ListDestinationsCommand => new DestinationsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.FillAsync("#svc-window", "22:00-06:00");
        await page.FillAsync("#svc-read-limit", "40 MiB/s");
        await page.FillAsync("#svc-max-backups", "3");
        await page.ClickAsync("[data-action=\"svc-settings-save\"]");

        var update = await harness.ReceivedAsync<UpdateServiceSettingsCommand>();
        Assert.AreEqual("22:00-06:00", update.BackgroundWindow);
        Assert.AreEqual("40 MiB/s", update.BackgroundReadLimit);
        Assert.AreEqual(3, update.MaxConcurrentBackups);

        await Expect(page.GetByText("Background window set to 22:00-06:00; it applies from the next pass."))
            .ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task ServiceSettingsCard_AWidthNotYetRunning_SaysItWaitsForARestart()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner"),
            GetServiceSettingsCommand => new ServiceSettingsResult(null, null, 3, EffectiveMaxConcurrentBackups: 2),
            ListDestinationsCommand => new DestinationsResult([]),
            ListBackupSetsCommand => new BackupSetsResult([]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await Expect(page.Locator("#svc-max-backups")).ToHaveValueAsync("3");
        await Expect(page.GetByText("The pool runs 2 until the service restarts.")).ToBeVisibleAsync();
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

        // A new set opens on the wizard's first step. Nothing reaches the
        // service until Create, so the walk below is three answered steps
        // and one command.
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        // Ticking a folder in the selection tree marks it as a root, and the
        // draft round-trips through validate_set_draft (350 ms debounce).
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        var validated = await harness.ReceivedAsync<ValidateSetDraftCommand>(draft =>
            draft.Roots is { } roots && roots.Contains("/data"));
        Assert.IsNotNull(validated);
        await page.ClickAsync("[data-action=\"wiz-next\"]");

        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"wiz-create\"]");

        // A NEW set has no saved baseline to compare against, so Create goes
        // straight to the upsert — no two-step consequence dialog.
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
    public async Task AHeldClaim_TheTypedWordArmsItsAcknowledgement_AndTheCommandNamesTheReplica()
    {
        // FR-DR-005: a replica a claim moved is held until this machine's
        // Owner acknowledges the claim. Its row says so, and the Owner's
        // button opens a dialog the typed word alone arms.
        var repositoryId = new string('c', 32);
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner") with { SignedInRole = "Owner" },
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult([Wire.Set()]),
            ListReplicaAttributionsCommand => new ReplicaAttributionsResult(
            [
                new ReplicaAttributionDescriptor(
                    repositoryId, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "rebuilt-laptop", true,
                    ClaimAwaitingAcknowledgement: true),
            ]),
            AcknowledgeReplicaClaimCommand => new ConfigurationChangeResult(["Acknowledged the claim."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await Expect(page.GetByText("claim awaiting acknowledgement")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=\"claim-ack-open\"]");
        await Expect(page.GetByText("rebuilt-laptop", new() { Exact = true }).Last).ToBeVisibleAsync();

        var acknowledge = page.Locator("#claim-ack-go");
        await Expect(acknowledge).ToBeDisabledAsync();
        await page.FillAsync("#confirm-word", "acknowledge");
        await Expect(acknowledge).ToBeEnabledAsync();
        await acknowledge.ClickAsync();

        var sent = await harness.ReceivedAsync<AcknowledgeReplicaClaimCommand>();
        Assert.AreEqual(repositoryId, sent.RepositoryId);
        await Expect(page.GetByText("Claim acknowledged")).ToBeVisibleAsync();
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

    [TestMethod]
    public async Task Adoption_ShowsWhatTheArchiveRecorded_ThenSendsOnlyWhatWasConfirmed()
    {
        // FR-DR-009: the first click shows the set as the archive recorded it
        // and adopts nothing; the second sends the preview's confirmation with
        // the folder the person re-pointed. The discovered row carries the
        // passphrase's real derivation under the archive's own salt, because
        // the console proves its derivation against it before sending anything.
        const string passphraseText = "Harbour-Kestrel-19-Vault-Door";
        var repositoryId = new string('c', 32);
        var confirmation = new string('9', 64);
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        var parameters = RepositoryCreationSettings.Default.KdfParameters;
        string sealingKey;
        using (var passphrase = Passphrase.Create(passphraseText))
        using (var authority = WriteOnlyDerivation.Derive(passphrase, parameters, salt, KdfValidationMode.OpenRepository))
        {
            sealingKey = Convert.ToHexStringLower(authority.Credential.SealingPublicKey);
        }

        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Wire.Describe("ready", signedInUser: "owner") with { SignedInRole = "Owner" },
            ListDestinationsCommand => new DestinationsResult([Vault]),
            ListBackupSetsCommand => new BackupSetsResult([]),
            DiscoverArchivesCommand => new ArchivesDiscoveredResult(
                "vault",
                [
                    new DiscoveredArchiveDescriptor(
                        repositoryId, 2, 1_700_000_000_000, "fallbackplan-agent/0.1", Convert.ToHexStringLower(salt),
                        parameters.MemoryKiB, parameters.Iterations, parameters.Parallelism, sealingKey,
                        SnapshotObjects: 3, HighestPublicationSequence: 12, OwnedBySet: null, SameInstallation: false),
                ],
                []),
            PreviewAdoptionCommand => new AdoptionPreviewResult(
                "vault", repositoryId, Wire.SetId, "docs",
                [new RecoveredRootDescriptor("/old/documents", "documents", Resolves: false)],
                "every 1h", [], ["**/*.tmp"], new RetentionPolicyDescriptor(KeepDaily: 7), 3,
                new string('5', 32), 1_700_000_000_000, AlreadyAdopted: false, Confirmation: confirmation,
                Lines: ["Recorded root folder '/old/documents' is not on this machine."]),
            AdoptArchiveCommand => new ArchiveAdoptedResult(
                Wire.SetId, "docs", repositoryId, [new BackupRootDescriptor("/new/documents", "documents")], [],
                "every 1h", [], ["**/*.tmp"], 3, null, null, WriterIdentityResumed: true, AlreadyAdopted: false,
                Lines: ["Adopted archive 'cccc' as set 'docs'."]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#config");

        await page.ClickAsync("[data-action=\"dest-discover\"]");
        await page.ClickAsync("[data-action=\"dest-adopt\"]");
        await page.FillAsync("#adopt-passphrase", passphraseText);
        await page.CheckAsync("#adopt-ack");
        await page.ClickAsync("[data-action=\"dest-adopt-go\"]");

        // The preview: the recorded path offered for re-pointing and flagged
        // as absent here, and the retention the set would delete by.
        await Expect(page.Locator("#adopt-root-0")).ToHaveValueAsync("/old/documents");
        await Expect(page.Locator(".badge.bad")).ToHaveTextAsync("not on this machine");
        await Expect(page.Locator("pre.report").First).ToContainTextAsync("keep 7 daily versions");
        var previewed = await harness.ReceivedAsync<PreviewAdoptionCommand>();
        Assert.AreEqual(repositoryId, previewed.RepositoryId);
        lock (harness.Clients.Client.Received)
        {
            Assert.IsEmpty(harness.Clients.Client.Received.OfType<AdoptArchiveCommand>());
        }

        await page.FillAsync("#adopt-root-0", "/new/documents");
        await page.ClickAsync("[data-action=\"dest-adopt-confirm\"]");

        var adopted = await harness.ReceivedAsync<AdoptArchiveCommand>();
        Assert.AreEqual(confirmation, adopted.Confirmation);
        var root = Assert.ContainsSingle(adopted.Roots!);
        Assert.AreEqual("/new/documents", root.Path);
        Assert.AreEqual("documents", root.Label);
        Assert.DoesNotContain("Kestrel", adopted.Envelope, StringComparison.Ordinal);
        await Expect(page.GetByText("Backup set 'docs' adopted")).ToBeVisibleAsync();
    }
}
