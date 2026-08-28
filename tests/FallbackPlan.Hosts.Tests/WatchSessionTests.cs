using System.Runtime.CompilerServices;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The watch connection is authenticated by the session its client already
/// holds (FR-SVC-006 with ADR-0045 §5's gate). A watch takes its own
/// connection, and each connection gets its own gate — so the session
/// presented on the command exchange proves nothing to the watch unless the
/// watch carries it. Before contract 1.20 it did not: on any installation
/// with accounts — every installation after setup — the gate answered every
/// watch with an empty stream, the stream ended at once, and the console
/// never received a single progress event however signed-in its viewer was.
/// The relay suites missed it because they fake the client below the
/// transport; this one runs the real listener, the real client, and the
/// real per-connection gates.
/// </summary>
[TestClass]
public sealed class WatchSessionTests : IDisposable
{
    private static readonly Domain.Configuration.Argon2Parameters Fast = new()
    {
        MemoryKiB = 64,
        Iterations = 1,
        Parallelism = 1,
    };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-watch-session-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }

        _timeout.Dispose();
    }

    /// <summary>Streams the hub; answers every command acknowledged.</summary>
    private sealed class StreamingService : IFallbackPlanService
    {
        public ProgressHub Hub { get; } = new();

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ServiceResult>(new AcknowledgedResult());

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            Hub.WatchAsync(cancellationToken);
    }

    private StreamingService _inner = null!;

    /// <summary>
    /// The AgentHost wiring — one gate PER CONNECTION, from a factory —
    /// over a real listener, with one owner account.
    /// </summary>
    private LocalServiceListener StartListener(SessionRegistry? sessions = null)
    {
        Directory.CreateDirectory(_root);
        _inner = new StreamingService();
        var users = UserStore.Open(_root, throttle: new UserStore.ThrottlePolicy(
            TimeSpan.FromMilliseconds(0.25), TimeSpan.FromMilliseconds(4)));
        users.Create("ben", "A-good-passw0rd9", parameters: Fast);
        var registry = sessions ?? new SessionRegistry();
        var log = new RecordingLogger();
        return LocalServiceListener.Start(
            () => new AuthenticatingService(_inner, users, registry, log), _root);
    }

    [TestMethod]
    public async Task AWatchOverTheRealBinding_CarriesTheSessionItsClientHolds()
    {
        await using var listener = StartListener();
        var inner = _inner;
        await using var client = await LocalServiceClient.ConnectAsync(_root, "test", Timeout);

        Assert.IsInstanceOfType<SessionResult>(
            await client.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), Timeout));

        // The watch opens a fresh connection with a fresh gate. The session
        // the client just minted must ride with it, or the gate answers an
        // empty stream and this enumeration ends without an event.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        var events = client.WatchAsync(stopping.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var progressEvent in events)
                {
                    return progressEvent;
                }

                return null;
            },
            stopping.Token);

        while (!watching.IsCompleted)
        {
            inner.Hub.Report(new JobProgress("job-1", JobState.Packing, 5, 3, 1, 0, 4096, 2048));
            await Task.Delay(50, Timeout);
        }

        var received = await watching;
        Assert.IsNotNull(
            received,
            "the signed-in client's watch must stream, not end as an anonymous refusal");
        Assert.AreEqual("job-1", received.Progress.JobId);
        await stopping.CancelAsync();
    }

    [TestMethod]
    public async Task AWatchClosedAndReopened_ReleasesItsSubscriptionAndReplaysTheLatest()
    {
        await using var listener = StartListener();
        var inner = _inner;
        await using var client = await LocalServiceClient.ConnectAsync(_root, "test", Timeout);
        Assert.IsInstanceOfType<SessionResult>(
            await client.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), Timeout));

        // Watch #1: prove it is live, then hang up.
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(Timeout))
        {
            var events = client.WatchAsync(first.Token);
            var watching = Task.Run(
                async () =>
                {
                    await foreach (var progressEvent in events)
                    {
                        return progressEvent;
                    }

                    return null;
                },
                first.Token);
            while (!watching.IsCompleted)
            {
                inner.Hub.Report(new JobProgress("job-1", JobState.Packing, 9, 4, 1, 0, 512, 256));
                await Task.Delay(25, Timeout);
            }

            Assert.IsNotNull(await watching);
            await first.CancelAsync();
        }

        // Watch #2, with NO further report: only the hub's latest-snapshot
        // replay can deliver anything, and only a fully re-established chain
        // — new connection, new gate, session re-presented — can carry it.
        using var second = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        var reopened = client.WatchAsync(second.Token);
        await foreach (var progressEvent in reopened)
        {
            Assert.AreEqual("job-1", progressEvent.Progress.JobId);
            Assert.AreEqual(JobState.Packing, progressEvent.Progress.State);
            await second.CancelAsync();
            return;
        }

        Assert.Fail("the reopened watch delivered nothing — the replay never crossed the transport");
    }

    [TestMethod]
    public async Task TheServiceStopping_EndsAWatchingClientsEnumerationCleanly()
    {
        var listener = StartListener();
        var inner = _inner;
        var client = await LocalServiceClient.ConnectAsync(_root, "test", Timeout);
        Assert.IsInstanceOfType<SessionResult>(
            await client.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), Timeout));

        var events = client.WatchAsync(Timeout);
        var watching = Task.Run(
            async () =>
            {
                var count = 0;
                await foreach (var _ in events)
                {
                    count++;
                }

                return count;
            },
            Timeout);

        // A report may or may not land before the stop — the property under
        // test holds either way.
        inner.Hub.Report(new JobProgress("job-1", JobState.Packing, 1, 0, 0, 0, 0, 0));
        await Task.Delay(50, Timeout);

        // The service goes away: the hub completes its watchers and the
        // listener closes its sockets. The client's enumeration must END —
        // no hang (the WaitAsync below is the assertion), no escaping
        // exception — which is what lets a console treat a stopped service
        // as "redial politely", not a crash.
        inner.Hub.Complete();
        await listener.DisposeAsync();

        var delivered = await watching.WaitAsync(Timeout);
        Assert.IsTrue(delivered >= 0, "unreachable: the count is what the clean ending returned");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task AWatchPresentingAnExpiredSession_EndsRatherThanStreaming()
    {
        // The refusal half of the session-carrying watch: the pump presents
        // the stale token, the gate refuses it, and the watch gets the
        // anonymous answer — an empty stream — rather than someone else's
        // progress or a hang.
        await using var listener = StartListener(new SessionRegistry(idleTimeout: TimeSpan.FromMilliseconds(1)));
        var inner = _inner;
        await using var client = await LocalServiceClient.ConnectAsync(_root, "test", Timeout);
        Assert.IsInstanceOfType<SessionResult>(
            await client.ExecuteAsync(new LoginCommand("ben", "A-good-passw0rd9"), Timeout));
        await Task.Delay(50, Timeout);

        inner.Hub.Report(new JobProgress("job-1", JobState.Packing, 1, 0, 0, 0, 0, 0));

        var received = 0;
        await foreach (var _ in client.WatchAsync(Timeout))
        {
            received++;
        }

        Assert.AreEqual(0, received, "an expired session's watch must end as anonymous, not stream");
    }
}
