using System.Net;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console relays and never derives (ADR-0036 §4): the browser's JSON
/// becomes exactly one contract command, and the service's result comes back
/// typed, discriminator and all. Transport trouble is an HTTP status; a
/// command-level refusal stays a <c>ServiceResult</c>, because "the service
/// said no" and "no service answered" must never be spelled the same.
/// </summary>
[TestClass]
public sealed class CommandRelayTests
{
    [TestMethod]
    public async Task PostedCommand_ReachesTheServiceVerbatim_AndTheResultComesBackTyped()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new JobAcceptedResult("job-7");

        using var request = harness.Command("""{"command":"run_backup","setName":"documents","full":true}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var received = Assert.ContainsSingle(harness.Clients.Client.Received);
        Assert.IsInstanceOfType<RunBackupCommand>(received, out var backup);
        Assert.AreEqual("documents", backup.SetName);
        Assert.IsTrue(backup.Full);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("job_accepted", body.RootElement.GetProperty("result").GetString());
        Assert.AreEqual("job-7", body.RootElement.GetProperty("jobId").GetString());
    }

    [TestMethod]
    public async Task NoticesVerbs_RelayLikeEveryOther_NoConsoleChangeNeeded()
    {
        // Contract 1.9's verbs reach the page through the same generic relay
        // — the console never enumerates commands, so a new one needs no
        // server change; this pins that for the notices pair.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new NoticesResult(
            [new NoticeDescriptor("ab12cd34", "set-changed:x", "the message", 9UL, null)]);

        using var request = harness.Command("""{"command":"list_notices","includeAcknowledged":true}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.ContainsSingle(harness.Clients.Client.Received);
        Assert.IsInstanceOfType<ListNoticesCommand>(received, out var list);
        Assert.IsTrue(list.IncludeAcknowledged);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("notices", body.RootElement.GetProperty("result").GetString());
        var notice = body.RootElement.GetProperty("notices")[0];
        Assert.AreEqual("ab12cd34", notice.GetProperty("id").GetString());
        Assert.AreEqual(9UL, notice.GetProperty("raisedAt").GetUInt64());
    }

    [TestMethod]
    public async Task ServiceRefusal_StaysAResult_WithTheReasonAName()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new ServiceError(
            ServiceErrorReason.Refused, "The writer role says no.");

        using var request = harness.Command("""{"command":"retention","apply":true}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("error", body.RootElement.GetProperty("result").GetString());
        Assert.AreEqual("Refused", body.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task EnumFields_TravelAsNames_NotNumbers()
    {
        // The page reads "CompletedWithFailures", never 14 — a number would
        // couple it to the enum's ordinals, which the contract does not pin.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new JobsResult(
            [new JobDescriptor("j1", "set1", JobState.CompletedWithFailures, 1, 2, null, null)]);

        using var request = harness.Command("""{"command":"list_jobs","activeOnly":false}""");
        using var response = await harness.Http.SendAsync(request);

        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "CompletedWithFailures");
    }

    [TestMethod]
    public async Task MalformedBody_IsAnsweredBadRequest_AndNeverReachesTheService()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = harness.Command("{not json");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "malformed_command");
        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task UnknownCommandName_IsAnsweredBadRequest()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var request = harness.Command("""{"command":"drop_everything"}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received);
    }

    [TestMethod]
    public async Task NoServiceListening_IsServiceUnavailable_NotAnInventedResult()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Unreachable = true;

        using var request = harness.Command("""{"command":"get_status"}""");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "service_unreachable");
    }
}
