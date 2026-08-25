using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console page in a real browser (ADR-0049). Wire names stay pinned in
/// <c>Web.Tests</c>; this suite owns the behaviour that only exists against a
/// real DOM — <c>showModal()</c> inertness, CSP enforcement, focus, real
/// downloads — which is exactly the class of defect that shipped invisibly
/// three times before it existed: a kit page whose buttons ate clicks, a
/// rebuild button typing could never enable, and a modal that froze two
/// screens while painting nothing.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class SetupCeremonyDomTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    private const string DeviceIdHex = "00112233445566778899aabbccddeeff";

    private static ServiceDescriptionResult Describe(string setupState, string? signedInUser = null) =>
        new(
            "1.18", "test", "vm", "/state", false, 0,
            ArchivesRoot: "/archives",
            RestoreGrantRecipient: Convert.ToHexStringLower(
                ContentSealing.PublicKeyOf(RandomNumberGenerator.GetBytes(32))),
            SetupState: setupState,
            DeviceId: DeviceIdHex,
            SignedInUser: signedInUser);

    [TestMethod]
    public async Task SetupCeremony_WalkedInARealBrowser_CompletesWithoutModalTraps()
    {
        await using var harness = await DomHarness.StartAsync();
        var provisioned = false;
        harness.Clients.Client.Respond = command =>
        {
            switch (command)
            {
                case DescribeServiceCommand:
                    return Describe(provisioned ? "users_required" : "setup_required");
                case ProvisionInstallationCommand:
                    provisioned = true;
                    return new ConfigurationChangeResult(["This installation is set up."]);
                case ConfirmRecoveryKitCommand:
                    return new ConfigurationChangeResult(["Recovery kit saved."]);
                default:
                    return new AcknowledgedResult();
            }
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        // Step 1: the acknowledgement gates the ceremony.
        await page.CheckAsync("#setup-ack");
        await page.ClickAsync("[data-action=\"setup-begin\"]");

        // Step 2: the strength meter (a real round trip) enables Continue.
        await page.FillAsync("#setup-pass", StrongPassphrase);
        await page.ClickAsync("[data-action=\"setup-to-confirm\"]");

        // Step 3: the confirmation enables the finish, and the finish runs a
        // real Argon2 derivation in the console before the fake acknowledges.
        await page.FillAsync("#setup-confirm", StrongPassphrase);
        await page.ClickAsync("[data-action=\"setup-finish\"]");

        // The kit page. The confirmation is inline and NO modal is open —
        // an open dialog here made every button on this page swallow clicks
        // while looking enabled, which is how a person got stranded.
        await Expect(page.Locator("[data-action=\"setup-kit-file\"]")).ToBeVisibleAsync();
        await Expect(page.GetByText("Passphrase accepted.")).ToBeVisibleAsync();
        Assert.IsFalse(
            await page.EvaluateAsync<bool>("document.getElementById('dialog').open"),
            "the ceremony must never sit under a modal");

        // Download is a real browser download, and taking it arms the chain.
        var download = await page.RunAndWaitForDownloadAsync(
            () => page.ClickAsync("[data-action=\"setup-kit-file\"]"));
        Assert.AreEqual("fallbackplan-recovery-kit.fbpkrkit", download.SuggestedFilename);

        await page.CheckAsync("#setup-kit-ack");
        await page.ClickAsync("[data-action=\"setup-kit-done\"]");

        // The end-of-ceremony dialog is an ordinary app dialog again — and it
        // must be SEEN, not merely open: nested under the hidden app panel it
        // painted nothing while freezing the whole document.
        var dialog = page.Locator("#dialog");
        await Expect(dialog).ToBeVisibleAsync();
        Assert.IsNotNull(await dialog.BoundingBoxAsync(), "an open dialog nobody can see is an inertness trap");

        Assert.IsTrue(
            harness.Clients.Client.Received.Any(received => received is ConfirmRecoveryKitCommand),
            "Finish must have sent confirm_recovery_kit");

        // Closing it hands the screen to sign-in, whose fields must accept
        // input — the second screen this trap froze.
        await page.ClickAsync("#dialog [data-action=\"close-dialog\"]");
        await page.FillAsync("#signin-user", "owner");
        Assert.AreEqual("owner", await page.InputValueAsync("#signin-user"));
    }

    [TestMethod]
    public async Task RebuildPage_TypingThePassphrase_EnablesBuildWithoutStealingFocus()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Describe("kit_required"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        await Expect(page.GetByText("Your recovery kit is still unsaved")).ToBeVisibleAsync();
        var build = page.Locator("[data-action=\"setup-rebuild-kit\"]");
        await Expect(build).ToBeDisabledAsync();

        // Real keystrokes: the defect was an input handler that only ever
        // enabled step 2's button, leaving this one dead until a stray
        // re-render — which also replaced the field mid-typing and stole its
        // focus, so the page read as one where every control was inert.
        await page.FocusAsync("#setup-pass");
        await page.Keyboard.TypeAsync(StrongPassphrase);

        await Expect(build).ToBeEnabledAsync();
        Assert.AreEqual(
            "setup-pass", await page.EvaluateAsync<string>("document.activeElement?.id"),
            "typing must not lose the field");
        Assert.AreEqual(StrongPassphrase, await page.InputValueAsync("#setup-pass"));
    }

    [TestMethod]
    public async Task JobsView_CancelShowsTheCancellingState()
    {
        var setId = new string('a', 32);
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            DescribeServiceCommand => Describe("ready", signedInUser: "owner"),
            ListJobsCommand => new JobsResult(
                [new JobDescriptor("job-1", setId, JobState.Publishing, nowMs, nowMs, null, null)]),
            ListBackupSetsCommand => new BackupSetsResult(
                [new BackupSetDescriptor(setId, "users", "/src", null, [], [], [])]),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{harness.TokenedUrl}#jobs");

        var cancel = page.Locator("[data-action=\"cancel-job\"]");
        await cancel.ClickAsync();

        // After an acknowledged cancel the card carries the state — a toast
        // alone was missable, and an unchanged card after a successful
        // command read as a click that did nothing.
        await Expect(cancel).ToBeDisabledAsync();
        await Expect(cancel).ToContainTextAsync("Cancelling…");
    }
}
