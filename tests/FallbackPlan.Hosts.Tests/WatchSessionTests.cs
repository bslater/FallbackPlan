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

    [TestMethod]
    public async Task AWatchOverTheRealBinding_CarriesTheSessionItsClientHolds()
    {
        Directory.CreateDirectory(_root);
        var inner = new StreamingService();
        var users = UserStore.Open(_root, throttle: new UserStore.ThrottlePolicy(
            TimeSpan.FromMilliseconds(0.25), TimeSpan.FromMilliseconds(4)));
        users.Create("ben", "A-good-passw0rd9", parameters: Fast);
        var sessions = new SessionRegistry();
        var log = new RecordingLogger();

        // The AgentHost wiring: one gate PER CONNECTION, from a factory.
        await using var listener = LocalServiceListener.Start(
            () => new AuthenticatingService(inner, users, sessions, log), _root);
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
}
