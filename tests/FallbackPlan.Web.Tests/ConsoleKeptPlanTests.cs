using System.Net;
using System.Text;
using System.Text.Json;
using FallbackPlan.Api;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The console runs a kept plan as it was approved (FR-RST-003, ADR-0118):
/// its endpoints read a kept plan with the reader the command line uses, and
/// hold a plan made now to it, asked of the service as the kept run, through
/// the source the person unlocked and under their session. What changed is
/// said in the words the command line says it.
/// </summary>
/// <remarks>
/// Reading asks the service nothing: the run a file keeps names no file of
/// the backup a person has not already got. Holding asks only for a plan, and
/// never runs anything; the run is the page's to ask, once nothing changed.
/// </remarks>
[TestClass]
public sealed class ConsoleKeptPlanTests
{
    private static readonly string SnapshotId = new('e', 32);

    private static readonly PlanRestoreCommand KeptRun = new(
        SnapshotId, null, Paths: ["docs/a.txt", "docs/b"], OutputDirectory: "/srv/restored", Target: "folder",
        Existing: "rename", InPlace: true);

    private static readonly RestorePlanResult KeptPlan = new(2, 9_000, [], ArchivedObjects: null);

    private static string KeptText => RestorePlanFile.Serialize(KeptRun, KeptPlan, new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero));

    [TestMethod]
    public async Task AKeptPlan_ReadsAsTheRunItWasKeptFor_AndAsksTheServiceNothing()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(Post(harness, "/api/restore-plan-read", new { text = KeptText }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var run = document.RootElement.GetProperty("run");
        Assert.AreEqual(SnapshotId, run.GetProperty("snapshotId").GetString());
        Assert.AreEqual("docs/b", run.GetProperty("paths")[1].GetString());
        Assert.AreEqual("/srv/restored", run.GetProperty("outputDirectory").GetString());
        Assert.AreEqual("rename", run.GetProperty("existing").GetString());
        Assert.IsTrue(run.GetProperty("inPlace").GetBoolean());
        StringAssert.StartsWith(document.RootElement.GetProperty("plannedAt").GetString(), "2026-10-27T09:30:00");
        Assert.IsEmpty(harness.Clients.Client.Received, "reading a kept plan asks the service nothing");
    }

    [TestMethod]
    [DataRow("""{"kind":"something-else","version":1}""", "not a kept restore plan")]
    [DataRow("not json", "not JSON")]
    public async Task AFileThatIsNotAKeptPlan_IsRefused_SayingWhy(string text, string why)
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(Post(harness, "/api/restore-plan-read", new { text }));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(why, document.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlanHeldToAKeptOne_IsAskedAsTheKeptRun_ThroughTheSourceUnlocked_UnderTheSession()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand ? KeptPlan : new AcknowledgedResult();

        using var request = Post(harness, "/api/restore-plan-held", new { text = KeptText, source = "src-1" });
        request.Headers.Add(WebConsoleHost.SessionHeader, "session-7");
        using var response = await harness.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, document.RootElement.GetProperty("changes").GetArrayLength(), "nothing changed");
        var asked = Assert.ContainsSingle(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
        Assert.AreEqual("src-1", asked.Source);
        Assert.AreEqual(SnapshotId, asked.SnapshotId);
        CollectionAssert.AreEqual(new[] { "docs/a.txt", "docs/b" }, asked.Paths!.ToArray());
        Assert.AreEqual("/srv/restored", asked.OutputDirectory);
        Assert.AreEqual("rename", asked.Existing);
        Assert.IsTrue(asked.InPlace);
        var received = harness.Clients.Client.Received;
        var resumed = received.FindIndex(command => command is ResumeSessionCommand { Token: "session-7" });
        Assert.IsGreaterThanOrEqualTo(0, resumed, "the browser's session is the one the plan is asked under");
        Assert.IsLessThan(received.FindIndex(command => command is PlanRestoreCommand), resumed);
        Assert.IsEmpty(received.OfType<RunRestoreCommand>(), "holding a plan runs nothing");
    }

    [TestMethod]
    public async Task APlanThatHasChangedSinceItWasKept_SaysEachChange()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new RestorePlanResult(1, 9_000, ["docs/a.txt"], Conflicts: 2)
            : new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(
            Post(harness, "/api/restore-plan-held", new { text = KeptText, source = "src-1" }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var changes = document.RootElement.GetProperty("changes").EnumerateArray().Select(change => change.GetString()).ToList();
        CollectionAssert.Contains(changes, "files: 2 kept, 1 now");
        CollectionAssert.Contains(changes, "missing now: docs/a.txt");
        CollectionAssert.Contains(changes, "conflicts: 0 kept, 2 now");
    }

    [TestMethod]
    public async Task APlanTheServiceRefused_GivesItsAnswer_AndNoChanges()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is PlanRestoreCommand
            ? new ServiceError(ServiceErrorReason.NotFound, "No restore source 'src-1' is open.")
            : new AcknowledgedResult();

        using var response = await harness.Http.SendAsync(
            Post(harness, "/api/restore-plan-held", new { text = KeptText, source = "src-1" }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("error", document.RootElement.GetProperty("result").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("changes", out _));
    }

    [TestMethod]
    public async Task AFileThatIsNotAKeptPlan_IsHeldToNothing_AndNothingIsAsked()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(
            Post(harness, "/api/restore-plan-held", new { text = "not json", source = "src-1" }));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
    }

    [TestMethod]
    [DataRow("/api/restore-plan-read")]
    [DataRow("/api/restore-plan-held")]
    public async Task WithoutTheToken_NothingIsReadOrAsked(string endpoint)
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.SendAsync(
            Post(harness, endpoint, new { text = KeptText, source = "src-1" }, token: "wrong"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(harness.Clients.Client.Received.OfType<PlanRestoreCommand>());
    }

    private static HttpRequestMessage Post(ConsoleHarness harness, string endpoint, object body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? harness.Auth.Token);
        return request;
    }
}
