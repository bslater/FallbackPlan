using FallbackPlan.Api;
using FallbackPlan.TestSupport;
using static Microsoft.Playwright.Assertions;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The sign-in arc (ADR-0045) walked by real keystrokes: the first-account
/// screen, the session the page then presents on every command, and the way
/// out. This gate shared the inertness hazard with setup — it is the screen
/// the invisible "Setup complete" modal froze — so its fields accepting real
/// input is part of what is under test.
/// </summary>
[TestClass]
[BrowserCondition]
public sealed class SignInDomTests
{
    [TestMethod]
    public async Task FirstAccount_CreateSignInAndOut_CarriesTheSessionInBetween()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var harness = await DomHarness.StartAsync();
        var signedIn = false;
        harness.Clients.Client.Respond = command =>
        {
            switch (command)
            {
                case DescribeServiceCommand:
                    return signedIn ? Wire.Describe("ready", signedInUser: "owner") : Wire.Describe("users_required");
                case CreateUserCommand:
                    return new AcknowledgedResult();
                case LoginCommand:
                    signedIn = true;
                    return new SessionResult("tok-1", "owner", "owner", now + 3_600_000, now + 28_800_000);
                case LogoutCommand:
                    signedIn = false;
                    return new AcknowledgedResult();
                default:
                    return new AcknowledgedResult();
            }
        };

        await using var context = await BrowserSession.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(harness.TokenedUrl);

        // users_required shows the first-account variant of the gate. The
        // heading, specifically — the submit button repeats the same words,
        // so the bare text would match two elements.
        await Expect(page.Locator("#signin h2")).ToHaveTextAsync("Create the first account");

        await page.FillAsync("#signin-user", "owner");
        await page.FillAsync("#signin-pass", "a local dev password");
        await page.ClickAsync("#signin-go");

        // Creation precedes login, and login mints the session.
        await harness.ReceivedAsync<CreateUserCommand>(created => created.Name == "owner");
        await harness.ReceivedAsync<LoginCommand>(login => login.User == "owner");

        // The gate yields, the chrome names who is acting…
        await Expect(page.Locator("#signed-in")).ToHaveTextAsync("owner");

        // …and every subsequent exchange presents the session: the console
        // injects resume_session ahead of each relayed command.
        var resumed = await harness.ReceivedAsync<ResumeSessionCommand>();
        Assert.AreEqual("tok-1", resumed.Token);

        await page.ClickAsync("#sign-out");
        await harness.ReceivedAsync<LogoutCommand>();
        await Expect(page.Locator("#signin")).ToBeVisibleAsync();
    }
}
