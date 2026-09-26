using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// A client that hangs up mid-command abandons the work, and abandoned work
/// stops. The 2026-08-25 service log holds the counter-example this suite
/// pins against: a preview scan that kept walking a source for 26,161,154 ms
/// — over seven hours — and answered into a pipe that had been broken since
/// the machine woke, because the pump handed every command the listener's
/// lifetime token rather than the connection's.
/// </summary>
[TestClass]
public sealed class AbandonedCommandTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api", Guid.NewGuid().ToString("n")[..12]);

    public AbandonedCommandTests() => Directory.CreateDirectory(_state);

    // A transport test that hangs is a transport test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task ClientDisconnectsMidCommand_CancelsTheWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService
        {
            RespondAsync = async (_, cancellationToken) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }

                return new AcknowledgedResult();
            },
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        // The answer never comes, so this task resolves only by the hang-up
        // below breaking it — observed at the end, asserted on never.
        var pending = client.ExecuteAsync(new ListSnapshotsCommand(), Timeout).AsTask();
        await started.Task.WaitAsync(Timeout);

        await client.DisposeAsync();

        // The assertion: the token the transport handed the command fires.
        // This is what lets OnReaderLaneAsync release the reader lane — the
        // service side of this wiring exists and is dead only because the
        // transport never pulls it.
        await cancelled.Task.WaitAsync(Timeout);

        try
        {
            await pending.WaitAsync(Timeout);
        }
        catch (Exception)
        {
            // The connection died under it — that is the point.
        }
    }

    [TestMethod]
    public async Task ClientDisconnectsMidCommand_SaysSoInTheConnectionLog()
    {
        // What the log said before this event existed: a CommandHandled line
        // seven hours late, then an unrelated-looking broken-pipe warning.
        // One line naming the verb and the wait turns that into a story.
        var log = new RecordingLogger();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeService
        {
            RespondAsync = async (_, cancellationToken) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }

                return new AcknowledgedResult();
            },
        };

        await using var listener = LocalServiceListener.Start(service, _state, log);
        var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);
        var pending = client.ExecuteAsync(new ListSnapshotsCommand(), Timeout).AsTask();
        await started.Task.WaitAsync(Timeout);

        await client.DisposeAsync();
        await cancelled.Task.WaitAsync(Timeout);

        // The line lands after the command observes its cancellation, so poll
        // rather than assert the instant the work stopped.
        while (!log.Records.Any(record => record.EventId == 3606))
        {
            await Task.Delay(10, Timeout);
        }

        var abandoned = log.Records.Single(record => record.EventId == 3606);
        Assert.AreEqual(LogLevel.Information, abandoned.Level);
        Assert.AreEqual(nameof(ListSnapshotsCommand), abandoned.Value("Verb"));

        try
        {
            await pending.WaitAsync(Timeout);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    [TestMethod]
    public async Task ClientDisconnectsMidWatch_ReleasesTheSubscription()
    {
        // The watch path's twin of the command tests above: a browser tab or
        // console that goes away must release its progress subscription NOW,
        // not on the next event's failed write — an idle service produces no
        // next event, so a write-failure-only reaping keeps a dead watcher
        // registered indefinitely and the service cannot tell "nobody is
        // watching" from "watcher not yet written to".
        var service = new FakeService
        {
            WatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WatchEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        // Open the watch and prove it is live: one event makes the round trip.
        var progressEvents = client.WatchAsync(Timeout);
        await using (var enumerator = progressEvents.GetAsyncEnumerator(Timeout))
        {
            await service.WatchStarted.Task.WaitAsync(Timeout);
            service.Emit(new JobProgress("job-1", JobState.Scanning, 1, 0, 0, 0, 0, 0));
            Assert.IsTrue(await enumerator.MoveNextAsync());
        }

        // Disposing the enumerator closed the watch connection — the client
        // hung up. No further event is ever emitted, so only the transport
        // noticing the hang-up can end the service-side enumeration.
        await client.DisposeAsync();

        await service.WatchEnded.Task.WaitAsync(Timeout);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        _timeout.Dispose();
    }
}
