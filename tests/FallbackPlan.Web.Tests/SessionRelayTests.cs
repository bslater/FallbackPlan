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
    public async Task ARefusedResume_AnswersTheRefusalWithoutSendingTheCommand()
    {
        // The service's resume refusal says exactly what to do ("log in
        // again"); the command's own refusal, sent blind afterwards, says
        // less and costs a second round trip. A browser sleeping through its
        // session's idle timeout retries every few seconds, so relaying the
        // doomed command doubles the traffic of an already-failing loop —
        // this is the wedge the 2026-08-25 service log recorded at 94
        // connections a minute.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is ResumeSessionCommand
            ? new ServiceError(ServiceErrorReason.Refused, "That session is not current — log in again.")
            : new AcknowledgedResult();

        using var request = harness.Command("""{"command":"get_status"}""");
        request.Headers.Add(WebConsoleHost.SessionHeader, new string('d', 64));
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsInstanceOfType<ResumeSessionCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received),
            "A refused resume must be the end of the exchange — the command it was presented for must not be sent.");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not current", body, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"error\"", body, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnOlderServicesResumeComplaint_DoesNotBlockTheCommand()
    {
        // A service that predates contract 1.16 answers resume_session itself
        // with InvalidArgument through its catch-all. That was always shrugged
        // off, and must stay so: only Refused — "that session is not current"
        // — ends the exchange.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is ResumeSessionCommand
            ? new ServiceError(ServiceErrorReason.InvalidArgument, "resume_session is not a command")
            : new AcknowledgedResult();

        using var request = harness.Command("""{"command":"get_status"}""");
        request.Headers.Add(WebConsoleHost.SessionHeader, new string('g', 64));
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.HasCount(2, harness.Clients.Client.Received);
        Assert.IsInstanceOfType<GetStatusCommand>(harness.Clients.Client.Received[1]);
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
