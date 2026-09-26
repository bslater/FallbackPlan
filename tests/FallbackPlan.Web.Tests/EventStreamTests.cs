using System.Net;
using System.Text.Json;
using FallbackPlan.Api;
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
    public async Task ProgressCarryingThePlan_PutsTheTotalsOnTheStream()
    {
        // Contract 1.20's counted plan, camelCased for the page: totalFiles
        // and totalBytes are what let the meter divide honestly and the ETA
        // exist at all.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Emit(new JobProgress(
            "job-9", JobState.Packing, 120, 120, 40, 0, 4096, 2048, TotalFiles: 500, TotalBytes: 1_000_000));

        using var response = await harness.Http.GetAsync(
            new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

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
        Assert.AreEqual(500, progress.GetProperty("totalFiles").GetInt64());
        Assert.AreEqual(1_000_000, progress.GetProperty("totalBytes").GetInt64());
    }

    [TestMethod]
    public async Task TheBrowserGoingAway_ClosesTheUpstreamWatch()
    {
        // The web host holds one service watch per browser stream, torn down
        // by RequestAborted when the browser leaves. The code threads the
        // token; this is the test that proves the chain actually fires —
        // without it, an abandoned tab would hold a live hub subscription
        // and the service could not tell "nobody is watching".
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Emit(new JobProgress("job-4", JobState.Reading, 1, 0, 0, 0, 10, 5));

        var response = await harness.Http.GetAsync(
            new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

        // Prove the stream is genuinely live before hanging up.
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(_timeout.Token));
        while (await reader.ReadLineAsync(_timeout.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.AreEqual(1, harness.Clients.Client.WatchesOpened);

        // The browser goes away.
        reader.Dispose();
        response.Dispose();

        while (harness.Clients.Client.WatchesEnded == 0)
        {
            await Task.Delay(10, _timeout.Token);
        }
    }

    [TestMethod]
    public async Task TwoEventStreams_EachReceivesTheEvents()
    {
        // Two browsers, two SSE requests, two independent upstream watches —
        // one emitted event reaches BOTH streams, not either-or.
        await using var harness = await ConsoleHarness.StartAsync();

        async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync()
        {
            var response = await harness.Http.GetAsync(
                new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead,
                _timeout.Token);
            return (response, new StreamReader(await response.Content.ReadAsStreamAsync(_timeout.Token)));
        }

        var first = await OpenAsync();
        var second = await OpenAsync();

        // Both upstream watches registered before the emit, so neither
        // stream's event can have been the buffered-before-any-watch one.
        while (harness.Clients.Client.WatchesOpened < 2)
        {
            await Task.Delay(10, _timeout.Token);
        }

        harness.Clients.Client.Emit(new JobProgress("job-5", JobState.Packing, 2, 1, 0, 0, 20, 10));

        async Task<string?> FirstDataLineAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(_timeout.Token) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return line;
                }
            }

            return null;
        }

        var firstLine = await FirstDataLineAsync(first.Reader);
        var secondLine = await FirstDataLineAsync(second.Reader);
        Assert.IsNotNull(firstLine);
        Assert.IsNotNull(secondLine);
        Assert.Contains("job-5", firstLine, StringComparison.Ordinal);
        Assert.Contains("job-5", secondLine, StringComparison.Ordinal);

        first.Reader.Dispose();
        first.Response.Dispose();
        second.Reader.Dispose();
        second.Response.Dispose();

        while (harness.Clients.Client.WatchesEnded < 2)
        {
            await Task.Delay(10, _timeout.Token);
        }
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

    [TestMethod]
    public async Task AStreamCarryingASession_PresentsItBeforeWatching()
    {
        // EventSource cannot set a header, so the session rides the query the
        // same way the console's own token does (ADR-0036 §4). Without this,
        // every watch on an installation with accounts is anonymous, the
        // gate answers it with an empty stream, and the browser redials for
        // ever — progress never arrives and the service logs a fresh watch
        // every two seconds, which is what the 2026-08-25 log shows.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = _ => new SessionResult(new string('e', 64), "ben", "Owner", 1, 2);
        harness.Clients.Client.Emit(new JobProgress("job-7", JobState.Reading, 1, 0, 0, 0, 10, 5));

        using var response = await harness.Http.GetAsync(
            new Uri(
                $"/api/events?token={harness.Auth.Token}&session={new string('e', 64)}",
                UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

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
        Assert.IsInstanceOfType<ResumeSessionCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received), out var resumed);
        Assert.AreEqual(new string('e', 64), resumed.Token);
    }

    [TestMethod]
    public async Task AStreamWhoseSessionIsRefused_SaysSoAndBacksOff()
    {
        // A watch that would stream nothing must not pretend to stream: the
        // page is told the session is dead — so it can show sign-in and stop
        // dialling — and the retry hint is raised from the streaming default
        // so a page that ignores the event polls politely instead of every
        // two seconds.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Respond = command => command is ResumeSessionCommand
            ? new ServiceError(ServiceErrorReason.Refused, "That session is not current — log in again.")
            : new AcknowledgedResult();

        using var response = await harness.Http.GetAsync(
            new Uri(
                $"/api/events?token={harness.Auth.Token}&session={new string('f', 64)}",
                UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var frames = await new StreamReader(await response.Content.ReadAsStreamAsync(_timeout.Token))
            .ReadToEndAsync(_timeout.Token);

        Assert.Contains("retry: 30000", frames, StringComparison.Ordinal);
        Assert.Contains("event: session", frames, StringComparison.Ordinal);
        Assert.Contains("not current", frames, StringComparison.Ordinal);
        Assert.IsInstanceOfType<ResumeSessionCommand>(
            Assert.ContainsSingle(harness.Clients.Client.Received),
            "A refused stream session must end the stream — the service must not be asked to watch.");
    }

    [TestMethod]
    public async Task AStreamWithNoSession_WatchesExactlyAsBefore()
    {
        // An installation with no accounts, or a service older than 1.16, has
        // nothing to present; sending resume_session anyway would break a
        // working console against an older service.
        await using var harness = await ConsoleHarness.StartAsync();
        harness.Clients.Client.Emit(new JobProgress("job-8", JobState.Reading, 1, 0, 0, 0, 10, 5));

        using var response = await harness.Http.GetAsync(
            new Uri("/api/events?token=" + harness.Auth.Token, UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            _timeout.Token);

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
        Assert.IsEmpty(
            harness.Clients.Client.Received,
            "With no session to present, the stream must not invent a resume.");
    }
}
