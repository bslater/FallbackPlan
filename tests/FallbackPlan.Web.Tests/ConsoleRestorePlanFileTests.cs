using System.Net;
using System.Text.Json;
using FallbackPlan.Api;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console keeps a restore's plan (FR-RST-003, ADR-0113): its endpoint
/// asks the service for the plan the wizard shows, and gives back the file
/// the command line keeps, in the contract's own words, for the browser to
/// save on the machine of the person who unlocked it.
/// </summary>
/// <remarks>
/// Only a plan is asked through it, and only under the console's token: a
/// plan names a backup's files. A plan the service refused gives no file,
/// only the service's answer, as the command relay would have given it.
/// </remarks>
[TestClass]
public sealed class ConsoleRestorePlanFileTests
{
    private const string PlanCommand = """
        {"command":"plan_restore","snapshotId":"0123456789abcdef","path":null,"source":"src-1",
         "paths":["docs/a.txt"],"outputDirectory":"/srv/restored","target":"folder","existing":"rename","inPlace":true}
        """;

    [TestMethod]
    public async Task APlanKept_IsTheFileTheCommandLineKeeps_AskedOfTheServiceAsTheWizardAsksIt()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new RestorePlanResult(2, 9_000, [], ArchivedObjects: ["docs/a.txt"])
            : new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(Post(harness, PlanCommand));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.IsNotNull(name);
        StringAssert.StartsWith(name, "restore-plan-01234567");
        StringAssert.EndsWith(name, ".json");
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.AreEqual(RestorePlanFile.Kind, root.GetProperty("kind").GetString());
        Assert.AreEqual(RestorePlanFile.Version, root.GetProperty("version").GetInt32());
        Assert.AreEqual("docs/a.txt", root.GetProperty("paths")[0].GetString());
        Assert.AreEqual("docs/a.txt", root.GetProperty("plan").GetProperty("archived_objects")[0].GetString());
        Assert.IsFalse(root.TryGetProperty("source", out _));

        var asked = Assert.ContainsSingle(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
        Assert.AreEqual("src-1", asked.Source, "the plan is asked through the source the wizard unlocked");
        Assert.AreEqual("rename", asked.Existing);
    }

    [TestMethod]
    public async Task ThePlanIsAsked_AsTheBrowsersPersonAsksIt_UnderTheirSession()
    {
        // The person who unlocked the source is the one the plan is asked as.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new RestorePlanResult(1, 42, [])
            : new AcknowledgedResult();

        using var request = Post(harness, PlanCommand);
        request.Headers.Add(WebConsoleHost.SessionHeader, "session-7");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var received = harness.Clients.Client.Received;
        var resumed = received.FindIndex(command => command is ResumeSessionCommand { Token: "session-7" });
        Assert.IsGreaterThanOrEqualTo(0, resumed, "the browser's session is the one the plan is asked under");
        Assert.IsLessThan(received.FindIndex(command => command is PlanRestoreCommand), resumed);
    }

    [TestMethod]
    public async Task APlanTheServiceRefused_GivesNoFile_OnlyItsAnswer()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new ServiceError(ServiceErrorReason.NotFound, "No restore source 'src-1' is open.")
            : new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(Post(harness, PlanCommand));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull(response.Content.Headers.ContentDisposition, "a refusal is not a plan to keep");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("error", document.RootElement.GetProperty("result").GetString());
        Assert.Contains("src-1", document.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnythingButAPlan_IsRefused_AndNothingIsAsked()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(Post(
            harness,
            """{"command":"run_restore","snapshotId":"0123456789abcdef","path":null,"outputDirectory":"/srv/restored"}"""));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<RunRestoreCommand>(), "a restore is never run through a plan's file");
    }

    [TestMethod]
    public async Task WithoutTheToken_NothingIsAsked()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(Post(harness, PlanCommand, token: "wrong"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
    }

    private static HttpRequestMessage Post(ConsoleHarness harness, string json, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/restore-plan-file", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? harness.Auth.Token);
        return request;
    }
}
