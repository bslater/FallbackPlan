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
/// The walk through the account step to a signed-in console is FR-USR-001's
/// first account, captured before setup completes, as a person meets it.
/// </remarks>
[TestClass]
[BrowserCondition]
public sealed class SetupCeremonyDomTests
{
    private const string StrongPassphrase = "Vault-Door-19-Kestrel-Harbour";

    private const string OwnerPassword = "Owner-Pass-42";

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
    public async Task SetupCeremony_CreatingTheFirstAccount_EndsSignedInAsIt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var provisioned = false;
        string? owner = null;
        var signedIn = false;
        harness.Clients.Client.Respond = command =>
        {
            switch (command)
            {
                // Answered as the service answers: users_required once the
                // passphrase is set, ready once an account exists, and the
                // account named only to a connection signed in as it.
                case DescribeServiceCommand:
                    return Describe(
                        !provisioned ? "setup_required" : owner is null ? "users_required" : "ready",
                        signedInUser: signedIn ? owner : null);
                case ProvisionInstallationCommand:
                    provisioned = true;
                    return new ConfigurationChangeResult(["This installation is set up."]);
                case CreateUserCommand create:
                    owner = create.Name;
                    return new UserListResult([new UserDescriptor(create.Name, "Owner", now, IsOwner: true)]);
                case LoginCommand login:
                    signedIn = login.User == owner;
                    return new SessionResult("tok-1", login.User, "Owner", now + 3_600_000, now + 28_800_000);
                default:
                    return new AcknowledgedResult();
            }
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        var thrown = new PageErrors(page);
        await page.GotoAsync(harness.TokenedUrl);

        await page.CheckAsync("#setup-ack");
        await page.ClickAsync("[data-action=\"setup-begin\"]");
        await page.FillAsync("#setup-pass", StrongPassphrase);
        await page.FillAsync("#setup-confirm", StrongPassphrase);
        var finish = page.Locator("[data-action=\"setup-finish\"]");
        await Expect(finish).ToBeEnabledAsync();
        await finish.ClickAsync();
        await Expect(page.GetByText("Create the first account")).ToBeVisibleAsync();

        // Create User arms on the console's own password verdict, a password
        // that is not the passphrase, and a confirmation that matches.
        await page.FillAsync("#setup-user", "owner");
        await page.FillAsync("#setup-user-pass", OwnerPassword);
        await page.FillAsync("#setup-user-confirm", OwnerPassword);
        var create = page.Locator("[data-action=\"setup-create-user\"]");
        await Expect(create).ToBeEnabledAsync();
        await create.ClickAsync();

        // The account is created in the bootstrap window and signed straight
        // in as, so the ceremony does not end at a form asking for what was
        // just typed.
        await harness.ReceivedAsync<CreateUserCommand>(created => created.Name == "owner");
        await harness.ReceivedAsync<LoginCommand>(login => login.User == "owner");

        // Then the ceremony is over: its gate gives way to the console, which
        // names who is acting and shows the report setup ends with. Both
        // commands above can succeed and the step still stay up, its button
        // disabled, past which only a refresh and a sign-in would get.
        await thrown.UntilAsync(Expect(page.Locator("#setup")).ToBeHiddenAsync());
        await Expect(page.Locator("#app")).ToBeVisibleAsync();
        await Expect(page.Locator("#signed-in")).ToHaveTextAsync("owner");
        await Expect(page.Locator("#dialog h3")).ToHaveTextAsync("Setup complete");
        await Expect(page.Locator("#dialog .report")).ToContainTextAsync("This installation is set up.");
    }

    [TestMethod]
    public async Task SetupCeremony_AcceptedPassphrase_OpensNoModalForTheRenderToClose()
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

        // Every showModal() the page makes, by the heading it shows, recorded
        // from before the page's own script runs.
        await page.AddInitScriptAsync(
            """
            globalThis.fbpModalsOpened = [];
            const showModal = HTMLDialogElement.prototype.showModal;
            HTMLDialogElement.prototype.showModal = function () {
              globalThis.fbpModalsOpened.push(this.querySelector("h3")?.textContent ?? "(no heading)");
              return showModal.call(this);
            };
            """);
        await page.GotoAsync(harness.TokenedUrl);

        await page.CheckAsync("#setup-ack");
        await page.ClickAsync("[data-action=\"setup-begin\"]");
        await page.FillAsync("#setup-pass", StrongPassphrase);
        await page.FillAsync("#setup-confirm", StrongPassphrase);
        var finish = page.Locator("[data-action=\"setup-finish\"]");
        await Expect(finish).ToBeEnabledAsync();
        await finish.ClickAsync();
        await Expect(page.GetByText("Create the first account")).ToBeVisibleAsync();

        // The walk above proves step 3 arrives with no modal over it, and
        // setupRender's heal makes that true of any path — including one that
        // opened a modal and had it closed before anybody saw it. The heal is
        // for accidents. An accepted passphrase is the ceremony's ordinary
        // path, so nothing on it may open a modal at all: one that is always
        // closed unseen is a report nobody can read, and a heal that fires on
        // every setup can no longer tell anyone about the accident it is for.
        var opened = await page.EvaluateAsync<string[]>("globalThis.fbpModalsOpened");
        Assert.IsEmpty(opened, "the ceremony opened a modal over itself: " + string.Join(", ", opened));

        // And the recorder is not blind: a modal opened now is seen.
        await page.EvaluateAsync("document.getElementById('dialog').showModal()");
        Assert.HasCount(1, await page.EvaluateAsync<string[]>("globalThis.fbpModalsOpened"));
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
