using System.Net;
using System.Text.Json;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Web.Tests;

/// <summary>
/// The event bridge: the contract's progress stream arrives at the browser as
/// server-sent events, token-in-query because <c>EventSource</c> cannot set a
/// header (ADR-0036 §4).
/// </summary>
[TestClass]
public sealed class EventStreamTests : IDisposable
{
    // A streaming test that hangs is a streaming test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [TestMethod]
    public async Task ReportedProgress_ArrivesAsAServerSentEvent()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Emit(new JobProgress("job-3", JobState.Reading, 10, 4, 2, 0, 1024, 512));

        using var response = await harness.Http.GetAsync(
            new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(_timeout.Token));
        string? dataLine = null;
        while (await reader.ReadLineAsync(_timeout.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLine = line["data: ".Length..];
                break;
            }
        }

        Assert.IsNotNull(dataLine, "No data frame arrived before the stream ended.");
        using var body = JsonDocument.Parse(dataLine);
        var progress = body.RootElement.GetProperty("progress");
        Assert.AreEqual("job-3", progress.GetProperty("jobId").GetString());
        Assert.AreEqual("Reading", progress.GetProperty("state").GetString());
        Assert.AreEqual(10, progress.GetProperty("filesSeen").GetInt64());
        Assert.IsGreaterThan(0, body.RootElement.GetProperty("sequence").GetInt64());
    }

    [TestMethod]
    public async Task EventStream_WithoutAToken_IsRefusedUnauthorized()
    {
        await using var harness = await ConsoleHarness.StartAsync();

        using var response = await harness.Http.GetAsync(
            new Uri("/api/events", UriKind.Relative), _timeout.Token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task EventStream_WithNoServiceListening_IsServiceUnavailable()
    {
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Unreachable = true;

        using var response = await harness.Http.GetAsync(
            new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative), _timeout.Token);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
