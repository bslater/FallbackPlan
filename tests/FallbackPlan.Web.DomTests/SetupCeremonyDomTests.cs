using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The console page in a real browser (ADR-0073). Wire names stay pinned in
/// <c>Web.Tests</c>; this suite owns the behaviour that only exists against a
/// real DOM — <c>showModal()</c> inertness, CSP enforcement, focus, real
/// downloads — which is exactly the class of defect that shipped invisibly
/// three times before it existed: a kit page whose buttons ate clicks, a
/// rebuild button typing could never enable, and a modal that froze two
/// screens while painting nothing.
/// </summary>
/// <remarks>
/// Re-homed onto the three-step ceremony when this line merged. The recovery
/// kit those first two defects lived on was withdrawn (ADR-0060): the
/// passphrase is the whole credential, nothing else is produced or saved, and
/// the confirmation shares step 2 with the passphrase instead of standing as a
/// step of its own. The defects are not: the field the strength verdict used
/// to replace mid-typing is still there, and the dialog that froze two screens
/// now stands over the account step. Both are asserted where they now live.
/// </remarks>
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

        // Step 2: the passphrase and its confirmation are one screen, and the
        // finish arms only on the service's strength verdict (a real round
        // trip) plus a confirmation that matches. The click runs a real Argon2
        // derivation in the console before the fake acknowledges.
        await page.FillAsync("#setup-pass", StrongPassphrase);
        await page.FillAsync("#setup-confirm", StrongPassphrase);
        var finish = page.Locator("[data-action=\"setup-finish\"]");
        await Expect(finish).ToBeEnabledAsync();
        await finish.ClickAsync();

        // Step 3, the first account — and it must arrive with NO modal over
        // it. An open dialog makes the whole document inert: nested under the
        // hidden app shell it painted nothing while freezing the two screens
        // behind it, and that is how a person got stranded on a page whose
        // every control looked enabled and ate clicks. The ceremony's rule is
        // now absolute — while it owns the screen, nothing sits over it.
        await Expect(page.GetByText("Create the first account")).ToBeVisibleAsync();
        Assert.IsFalse(
            await page.EvaluateAsync<bool>("document.getElementById('dialog').open"),
            "the ceremony must never sit under a modal");

        // The proof that it is not inert: the fields take real input.
        await page.FillAsync("#setup-user", "owner");
        Assert.AreEqual("owner", await page.InputValueAsync("#setup-user"));
    }

    [TestMethod]
    public async Task UnfinishedCeremony_TypingThePassphrase_ArmsTheFinishWithoutStealingFocus()
    {
        await using var harness = await DomHarness.StartAsync();
        harness.Clients.Client.Respond = command => command switch
        {
            // A service on contracts 1.14–1.28 reports kit_required between
            // setup_required and ready. The kit it names is gone (ADR-0060),
            // but the state still says the ceremony never finished, so the
            // console puts the ceremony up rather than the console.
            DescribeServiceCommand => Describe("kit_required"),
            _ => new AcknowledgedResult(),
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        await page.CheckAsync("#setup-ack");
        await page.ClickAsync("[data-action=\"setup-begin\"]");

        var finish = page.Locator("[data-action=\"setup-finish\"]");
        await Expect(finish).ToBeDisabledAsync();

        // Real keystrokes: the defect was a strength answer that re-rendered
        // the step, replacing the very field being typed in — focus landed on
        // a fresh element with the caret wherever the browser put it, so the
        // cursor jumped on every debounced answer and in-flight keystrokes
        // died. The verdict must patch the meter and the button in place.
        await page.FocusAsync("#setup-pass");
        await page.Keyboard.TypeAsync(StrongPassphrase);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(StrongPassphrase);

        await Expect(finish).ToBeEnabledAsync();
        Assert.AreEqual(
            "setup-confirm", await page.EvaluateAsync<string>("document.activeElement?.id"),
            "typing must not lose the field");
        Assert.AreEqual(StrongPassphrase, await page.InputValueAsync("#setup-pass"));
        Assert.AreEqual(StrongPassphrase, await page.InputValueAsync("#setup-confirm"));
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
