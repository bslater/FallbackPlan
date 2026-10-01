using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// Applying retention from the console, walked in a real browser (FR-GC-008,
/// ADR-0055): the dialog asks for the passphrase beside the typed word, the
/// console process derives the reclaim grant from it, and the service is sent
/// the sealed grant, never the passphrase. Before this the dialog sent the
/// apply bare, and every set refused it.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class RetentionApplyDomTests
{
    private const string PassphraseText = "the console retention passphrase";

    [TestMethod]
    public async Task ApplyRetention_ThePassphraseBecomesASealedGrant_AndTheReportIsShown()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var authority = Derive(PassphraseText, salt);

        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, salt, authority);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await page.ClickAsync("[data-action=\"retention-apply\"]");
        var go = page.Locator("#retention-apply-go");
        await Expect(go).ToBeDisabledAsync();

        // The word alone does not arm it; the passphrase typed last does.
        await page.FillAsync("#confirm-word", "apply");
        await Expect(go).ToBeDisabledAsync();
        await page.FillAsync("#retention-passphrase", PassphraseText);
        await Expect(go).ToBeEnabledAsync();
        await go.ClickAsync();

        await Expect(page.Locator("#dialog").GetByText("Retention — applied")).ToBeVisibleAsync();
        await Expect(page.Locator("#dialog")).ToContainTextAsync("docs: 1 snapshot tombstoned (fake)");

        var sent = await harness.ReceivedAsync<RetentionCommand>();
        Assert.IsTrue(sent.Apply);
        CollectionAssert.AreEqual(
            authority.ReclaimKeySeed.ToArray(),
            WriteOnlyProvisioning.OpenGrant(recipientScalar, Convert.FromHexString(sent.ReclaimGrants![Wire.SetId])),
            "the grant is the passphrase's own reclaim key, sealed to the service");
    }

    [TestMethod]
    public async Task ApplyRetention_TheWrongPassphrase_IsSaid_AndNothingIsApplied()
    {
        var recipientScalar = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(KekDerivation.SaltLength);
        using var authority = Derive(PassphraseText, salt);

        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = Service(recipientScalar, salt, authority);
        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#maintenance");

        await page.ClickAsync("[data-action=\"retention-apply\"]");
        await page.FillAsync("#retention-passphrase", "not the passphrase of anything here");
        await page.FillAsync("#confirm-word", "apply");
        await page.ClickAsync("#retention-apply-go");

        await Expect(page.Locator("#dialog")).ToContainTextAsync("does not open");
        await Expect(page.Locator("#retention-passphrase")).ToBeVisibleAsync();
        lock (harness.Clients.Client.Received)
        {
            Assert.IsEmpty(harness.Clients.Client.Received.OfType<RetentionCommand>());
        }
    }

    private static RepositoryReadAuthority Derive(string passphraseText, byte[] salt)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(
            passphrase, RepositoryCreationSettings.Default.KdfParameters, salt, KdfValidationMode.OpenRepository);
    }

    private static Func<ServiceCommand, ServiceResult> Service(
        byte[] recipientScalar, byte[] salt, RepositoryReadAuthority authority)
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
            RetentionCommand => new RetentionResult(["docs: 1 snapshot tombstoned (fake)"]),
            _ => new AcknowledgedResult(),
        };
    }
}
