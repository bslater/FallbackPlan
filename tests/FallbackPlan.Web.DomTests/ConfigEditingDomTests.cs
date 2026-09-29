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
/// write-only provisioning ceremony, and the adoption ceremony's preview and
/// confirmation (FR-DR-009) — each asserting the command its dialog claims
/// to send.
/// </summary>
/// <remarks>
/// Re-homed onto the sectioned set editor when this line merged: the single
/// form this suite filled in one pass became a summary with a Change… dialog
/// per section and one confirm at the end. What is asserted — the tree's tick
/// becomes a root, the draft is validated as it is built, and the editor sends
/// exactly one upsert carrying the name, root and destination — is unchanged.
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

        // The editor opens on its summary: what the set says now, one row per
        // section, each behind its own Change… dialog. Nothing reaches the
        // service until the single confirm at the end, so the walk below is
        // three staged sections and one command.
        await page.ClickAsync("[data-action=\"sec-locations\"]");

        // Ticking a folder in the selection tree marks it as a root, and the
        // draft round-trips through validate_set_draft (350 ms debounce).
        await page.CheckAsync("input.mark[data-mark-path=\"/data\"]");
        var validated = await harness.ReceivedAsync<ValidateSetDraftCommand>(draft =>
            draft.Roots is { } roots && roots.Contains("/data"));
        Assert.IsNotNull(validated);
        await page.ClickAsync("[data-action=\"sec-save\"]");

        await page.ClickAsync("[data-action=\"sec-name\"]");
        await page.FillAsync("#set-name", "docs");
        await page.ClickAsync("[data-action=\"sec-save\"]");

        await page.ClickAsync("[data-action=\"sec-destinations\"]");
        await page.CheckAsync("[data-dest-check=\"vault\"]");
        await page.ClickAsync("[data-action=\"sec-save\"]");

        await page.ClickAsync("[data-action=\"set-confirm-all\"]");

        // A NEW set has no saved baseline, so the confirm is non-material and
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
