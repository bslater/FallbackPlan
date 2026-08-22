using System.Net;
using FallbackPlan.Api;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console carries a viewer's service session without ever holding one
/// (FR-USR-001, FR-USR-003; ADR-0045 §5).
/// </summary>
/// <remarks>
/// The distinction this suite exists to pin: the console's bearer token says
/// this browser may talk to this console, and the session says which person is
/// acting on the service behind it. A console that cached one session would
/// make every action attributable to whoever signed in first — the problem
/// ADR-0045 exists to solve, moved one hop rather than fixed.
/// </remarks>
[TestClass]
public sealed class SessionRelayTests
{
    [TestMethod]
    public async Task ARequestCarryingASession_PresentsItBeforeTheCommand()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using var request = harness.Command("""{"command":"get_status"}""");
        request.Headers.Add(WebConsoleHost.SessionHeader, new string('a', 64));
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.HasCount(2, harness.Clients.Client.Received);

        Assert.IsInstanceOfType<ResumeSessionCommand>(harness.Clients.Client.Received[0], out var resumed);
        Assert.AreEqual(new string('a', 64), resumed.Token);
        Assert.IsInstanceOfType<GetStatusCommand>(harness.Clients.Client.Received[1]);
    }

    [TestMethod]
    public async Task ARequestWithNoSession_RelaysTheCommandAlone()
    {
        // An installation with no accounts, or a service older than 1.16, has
        // nothing to present. Sending resume_session anyway would turn a
        // working console into a broken one against an older service.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsInstanceOfType<GetStatusCommand>(Assert.ContainsSingle(harness.Clients.Client.Received));
    }

    [TestMethod]
    public async Task TwoBrowsers_PresentTheirOwnSessions()
    {
        // The whole reason the token rides the request rather than living in
        // this process: one console relays for several people, and each acts
        // as themselves.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new AcknowledgedResult();

        using (var first = harness.Command("""{"command":"get_status"}"""))
        {
            first.Headers.Add(WebConsoleHost.SessionHeader, new string('b', 64));
            using var _ = await harness.Http.SendAsync(first);
        }

        using (var second = harness.Command("""{"command":"get_status"}"""))
        {
            second.Headers.Add(WebConsoleHost.SessionHeader, new string('c', 64));
            using var _ = await harness.Http.SendAsync(second);
        }

        var presented = harness.Clients.Client.Received
            .OfType<ResumeSessionCommand>()
            .Select(command => command.Token)
            .ToArray();

        CollectionAssert.AreEqual(new[] { new string('b', 64), new string('c', 64) }, presented);
    }

    [TestMethod]
    public async Task TheLoginVerb_RelaysLikeAnyOther()
    {
        // The console enumerates no commands, so signing in needs no server
        // change — this pins that for the verb where it would be most
        // tempting to add one.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new SessionResult("token", "ben", "Owner", 1, 2);

        using var request = harness.Command("""{"command":"login","user":"ben","password":"secret"}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsInstanceOfType<LoginCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var login);
        Assert.AreEqual("ben", login.User);
    }
}
