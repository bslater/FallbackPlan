using System.Net;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console's stand-in for the local socket's peer credentials (ADR-0036
/// §3): a per-run token on every data request, and loopback-only hosts
/// everywhere. Each refusal is asserted against the browser-facing status code
/// and the closed-set error code, because the page branches on those and never
/// on prose.
/// </summary>
[TestClass]
public sealed class ConsoleAuthTests
{
    [TestMethod]
    public async Task Command_WithoutAToken_IsRefusedUnauthorized()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.PostAsync(
            new Uri("/api/command", UriKind.Relative),
            new StringContent("""{"command":"list_jobs","activeOnly":false}"""));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "token_missing_or_wrong");
        Assert.IsEmpty(harness.Clients.Client.Received, "A refused request must never reach the service.");
    }

    [TestMethod]
    public async Task Command_WithTheWrongToken_IsRefusedUnauthorized()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = harness.Command(
            """{"command":"list_jobs","activeOnly":false}""", tokenOverride: new string('0', 64));
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task Command_WithTheTokenAsABearerHeader_IsAnswered()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = harness.Command("""{"command":"list_jobs","activeOnly":false}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Command_WithTheTokenAsAQueryParameter_IsAnswered()
    {
        // EventSource cannot set headers, so the query form must work — and
        // must be the same fixed-time comparison, not a second path.
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.PostAsync(
            new Uri("/api/command?token=" + harness.Auth.Token, UriKind.Relative),
            new StringContent("""{"command":"list_jobs","activeOnly":false}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AnyRequest_NamingANonLoopbackHost_IsRefusedForbidden()
    {
        // The DNS-rebinding shape: the attacker's hostname resolves to
        // 127.0.0.1, so the socket is ours but the Host header is theirs.
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = harness.Command("""{"command":"list_jobs","activeOnly":false}""");
        request.Headers.Host = "attacker.example";
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "host_not_loopback");
        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task StaticPage_NamingANonLoopbackHost_IsRefusedToo()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Host = "attacker.example";
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task StaticPage_OverLoopback_NeedsNoToken()
    {
        // The shell holds no data and no secrets — the token arrives with the
        // operator through the printed URL, not from the page.
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.GetAsync(new Uri("/", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "app.js");
    }

    [TestMethod]
    public async Task EveryAnswer_CarriesTheSecurityHeaders()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.GetAsync(new Uri("/", UriKind.Relative));

        StringAssert.Contains(
            response.Headers.GetValues("Content-Security-Policy").Single(), "default-src 'self'");
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.AreEqual("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [TestMethod]
    public async Task TokenisedUrl_CarriesTheTokenTheConsoleAccepts()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        StringAssert.Contains(harness.Console.TokenisedUrl, "?token=" + harness.Auth.Token);
        StringAssert.Contains(harness.Console.BaseAddress.ToString(), "127.0.0.1");
    }
}
