using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// The local binding (ADR-0028 §5, FR-SVC-003): a Unix domain socket or named
/// pipe, authenticated by the operating system. No password, no token file, and
/// — the assertion that matters most — no port.
/// </summary>
[TestClass]
public sealed class LocalBindingTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api", Guid.NewGuid().ToString("n")[..12]);

    public LocalBindingTests() => Directory.CreateDirectory(_state);

    // A transport test that hangs is a transport test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task LocalBinding_CommandSentOverTheEndpoint_ReturnsTheServicesResult()
    {
        var service = new FakeService
        {
            Respond = _ => new JobAcceptedResult("job-1"),
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        var result = await client.ExecuteAsync(
            new RunBackupCommand("documents", Full: false), Timeout);

        Assert.IsInstanceOfType<JobAcceptedResult>(result, out var accepted);
        Assert.AreEqual("job-1", accepted.JobId);
        Assert.IsInstanceOfType<RunBackupCommand>(Assert.ContainsSingle(service.Received), out var received);
        Assert.AreEqual("documents", received.SetName);
    }

    [TestMethod]
    public async Task LocalBinding_SeveralCommandsOnOneConnection_PreservesTheirOrder()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        for (var i = 0; i < 5; i++)
        {
            var result = await client.ExecuteAsync(new ListSnapshotsCommand(), Timeout);
            Assert.IsInstanceOfType<AcknowledgedResult>(result);
        }

        Assert.AreEqual(5, service.Received.Count);
    }

    [TestMethod]
    public async Task LocalBinding_ServiceReturnsAnError_CrossesTheBoundaryAsAResult()
    {
        // NFR-PORT-004. An exception thrown on the far side loses its type and
        // its stack means nothing here, so every outcome a caller might handle
        // is a value.
        var service = new FakeService
        {
            Respond = _ => new ServiceError(ServiceErrorReason.NotFound, "No snapshot 'abcd' exists."),
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        var result = await client.ExecuteAsync(
            new ListDirectoryCommand("abcd", null), Timeout);

        Assert.IsInstanceOfType<ServiceError>(result, out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
        Assert.Contains("abcd", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task LocalBinding_ClientIsWatching_StreamsProgressEvents()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        stopping.CancelAfter(TimeSpan.FromSeconds(20));

        // The list is written by the watching task and read by this one, so
        // every touch is under the lock. Reading a List<T> while another thread
        // appends to it is undefined, not merely stale.
        var seen = new List<JobProgressEvent>();
        int SeenCount()
        {
            lock (seen)
            {
                return seen.Count;
            }
        }

        // Connecting happens on this thread: WatchAsync opens the watch
        // connection when called, not when first enumerated, so the service is
        // already streaming to it by the time the emit loop below starts.
        var progressEvents = client.WatchAsync(stopping.Token);

        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in progressEvents)
                {
                    lock (seen)
                    {
                        seen.Add(progress);
                        if (seen.Count == 3)
                        {
                            return;
                        }
                    }
                }
            },
            stopping.Token);

        // Emission still repeats. Opening the connection eagerly closes the
        // window on this side, but the service accepts it asynchronously, so an
        // event emitted before the accept completes would still be sent to
        // nobody — a real property of the transport rather than something to
        // assert away.
        while (!watching.IsCompleted && SeenCount() < 3)
        {
            service.Emit(new JobProgress("job-1", JobState.Scanning, 10, 4, 1, 0, 4096, 2048));
            await Task.Delay(50, stopping.Token);
        }

        await watching;

        lock (seen)
        {
            Assert.AreEqual(3, seen.Count);
            Assert.AreEqual(JobState.Scanning, seen[0].Progress.State);
            Assert.IsTrue(seen[1].Sequence > seen[0].Sequence, "progress sequence must be monotonic so a client can spot a gap");
        }
    }

    [TestMethod]
    public async Task LocalBinding_ProgressCarryingThePlan_RoundTripsTheTotals()
    {
        // Contract 1.20's additive fields: the counted plan survives the
        // socket, and a report without one still answers null rather than
        // zero — the client's cue for an indeterminate meter.
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        var progressEvents = client.WatchAsync(stopping.Token);

        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in progressEvents)
                {
                    return progress.Progress;
                }

                return null;
            },
            stopping.Token);

        while (!watching.IsCompleted)
        {
            service.Emit(new JobProgress(
                "job-1", JobState.Packing, 120, 120, 40, 1, 4096, 2048, TotalFiles: 500, TotalBytes: 1_000_000));
            await Task.Delay(50, stopping.Token);
        }

        var received = await watching;
        Assert.IsNotNull(received);
        Assert.AreEqual(500L, received.TotalFiles);
        Assert.AreEqual(1_000_000L, received.TotalBytes);
    }

    [TestMethod]
    public async Task Connect_WhenNoServiceIsListening_ShouldThrowWithAStatedReason()
    {
        var failure = await Assert.ThrowsExactlyAsync<ServiceConnectionException>(
            () => LocalServiceClient.ConnectAsync(_state, "test", Timeout).AsTask());

        Assert.Contains("No service is listening", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ServiceBinding_DefaultConfiguration_BindsAFilesystemEndpointAndNoPort()
    {
        // FR-SVC-003, and the property this whole binding exists for:
        // topologies 1 and 2 carry no port and no credential to talk to your
        // own machine.
        //
        // Asserted on the endpoint rather than by scanning the machine's
        // listening ports: test projects run in parallel and their runners own
        // ports of their own, so a global scan would be measuring the test
        // harness rather than the service.
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);
        await client.ExecuteAsync(new DescribeServiceCommand(), Timeout);

        Assert.IsFalse(IPEndPoint.TryParse(listener.Address, out _));

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("fallbackplan-", listener.Address, StringComparison.Ordinal);
            Assert.DoesNotContain(":", listener.Address, StringComparison.Ordinal);
        }
        else
        {
            // A real filesystem object, in a directory only its owner may write.
            Assert.IsTrue(File.Exists(listener.Address));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_state));
        }
    }

    [TestMethod]
    public void RemoteBinding_Options_AreValidatedForShapeAndNothingMore()
    {
        // A default install: disabled, and honoured (a port is opened nowhere).
        Assert.IsTrue(RemoteBindingOptions.Disabled.TryValidate(out var noReason));
        Assert.IsNull(noReason);

        // Enabled without an interface is refused — "which interface is this
        // on?" must be answerable from configuration (FR-SVC-003).
        var unnamed = new RemoteBindingOptions { Enabled = true, Port = 8443 };
        Assert.IsFalse(unnamed.TryValidate(out var unnamedReason));
        Assert.Contains("name the interface", unnamedReason!, StringComparison.Ordinal);

        // A bad port is refused.
        var badPort = new RemoteBindingOptions { Enabled = true, Interface = "0.0.0.0", Port = 0 };
        Assert.IsFalse(badPort.TryValidate(out var portReason));
        Assert.Contains("port", portReason!, StringComparison.Ordinal);

        // Well-formed is now honoured: the pairing that stands behind the
        // binding is a separate act, no longer a reason to refuse the options.
        // The listener admits only a pinned peer (ADR-0030) — proven over the
        // wire in Protocol.Tests/PeerWireTests and end to end in
        // Hosts.Tests/RemoteBindingTests.
        var wellFormed = new RemoteBindingOptions { Enabled = true, Interface = "0.0.0.0", Port = 8443 };
        Assert.IsTrue(wellFormed.TryValidate(out var okReason));
        Assert.IsNull(okReason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "SO_PEERCRED is the Linux answer; macOS and Windows have their own.")]
    [UnsupportedOSPlatform("windows")]
    public async Task PeerCredentials_OverAUnixSocket_NamesTheCallerRatherThanShruggingAtIt()
    {
        // This runs on every accepted local connection and its result had no
        // line coverage on either outcome — the dispatch into the Linux branch
        // was covered, neither what it returns nor its fallback was. That is
        // the shape of a call whose answer nobody checks, and the answer is
        // about to matter: it is informational only until Q19 settles console
        // identity, and an authorization input the moment it does not.
        //
        // Read over a real socket pair rather than through the listener,
        // because the listener's accept loop is a background task and a test
        // that raced its own teardown is how the coverage went missing in the
        // first place.
        var path = Path.Combine(_state, "creds.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), Timeout);
        using var accepted = await listener.AcceptAsync(Timeout);

        var identity = PeerCredentials.Read(accepted);

        Assert.IsTrue(identity.IsKnown, "the platform does report the caller, so an Unknown here is a defect");
        Assert.AreEqual(Environment.ProcessId, (int)identity.ProcessId, "the caller is this very process");
        Assert.IsGreaterThanOrEqualTo(0L, identity.UserId);
        Assert.Contains("pid", identity.Name!, StringComparison.Ordinal);
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
