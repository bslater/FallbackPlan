using System.Net;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// The local binding (ADR-0028 §5, FR-SVC-003): a Unix domain socket or named
/// pipe, authenticated by the operating system. No password, no token file, and
/// — the assertion that matters most — no port.
/// </summary>
public sealed class LocalBindingTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "fbp-api", Guid.NewGuid().ToString("n")[..12]);

    public LocalBindingTests() => Directory.CreateDirectory(_state);

    // A transport test that hangs is a transport test that tells you nothing.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private CancellationToken Timeout => _timeout.Token;

    [Fact]
    public async Task A_command_travels_to_the_service_and_its_result_comes_back()
    {
        var service = new FakeService
        {
            Respond = _ => new JobAcceptedResult("job-1"),
        };

        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        var result = await client.ExecuteAsync(
            new RunBackupCommand("documents", Full: false), Timeout);

        var accepted = Assert.IsType<JobAcceptedResult>(result);
        Assert.Equal("job-1", accepted.JobId);
        var received = Assert.IsType<RunBackupCommand>(Assert.Single(service.Received));
        Assert.Equal("documents", received.SetName);
    }

    [Fact]
    public async Task Several_commands_travel_over_one_connection_in_order()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        for (var i = 0; i < 5; i++)
        {
            var result = await client.ExecuteAsync(new ListSnapshotsCommand(), Timeout);
            Assert.IsType<AcknowledgedResult>(result);
        }

        Assert.Equal(5, service.Received.Count);
    }

    [Fact]
    public async Task An_error_result_crosses_the_boundary_as_a_result_not_an_exception()
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

        var error = Assert.IsType<ServiceError>(result);
        Assert.Equal(ServiceErrorReason.NotFound, error.Reason);
        Assert.Contains("abcd", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_events_stream_to_a_watching_client()
    {
        var service = new FakeService();
        await using var listener = LocalServiceListener.Start(service, _state);
        await using var client = await LocalServiceClient.ConnectAsync(_state, "test", Timeout);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        stopping.CancelAfter(TimeSpan.FromSeconds(20));

        var seen = new List<JobProgressEvent>();
        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in client.WatchAsync(stopping.Token))
                {
                    seen.Add(progress);
                    if (seen.Count == 3)
                    {
                        return;
                    }
                }
            },
            stopping.Token);

        // The watch takes its own connection, so give it a moment to be
        // accepted before emitting — otherwise this test races the transport
        // rather than testing it.
        while (!watching.IsCompleted && seen.Count < 3)
        {
            service.Emit(new JobProgress("job-1", JobState.Scanning, 10, 4, 1, 0, 4096, 2048));
            await Task.Delay(50, stopping.Token);
        }

        await watching;

        Assert.Equal(3, seen.Count);
        Assert.Equal(JobState.Scanning, seen[0].Progress.State);
        Assert.True(seen[1].Sequence > seen[0].Sequence, "progress sequence must be monotonic so a client can spot a gap");
    }

    [Fact]
    public async Task Connecting_when_no_service_is_listening_fails_with_a_stated_reason()
    {
        var failure = await Assert.ThrowsAsync<ServiceConnectionException>(
            () => LocalServiceClient.ConnectAsync(_state, "test", Timeout).AsTask());

        Assert.Contains("No service is listening", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_service_binds_a_filesystem_endpoint_and_never_a_port()
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

        Assert.False(IPEndPoint.TryParse(listener.Address, out _));

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("fallbackplan-", listener.Address, StringComparison.Ordinal);
            Assert.DoesNotContain(":", listener.Address, StringComparison.Ordinal);
        }
        else
        {
            // A real filesystem object, in a directory only its owner may write.
            Assert.True(File.Exists(listener.Address));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_state));
        }
    }

    [Fact]
    public void The_remote_binding_is_absent_until_enabled_and_refuses_without_pairing()
    {
        Assert.True(RemoteBindingOptions.Disabled.TryValidate(out var noReason));
        Assert.Null(noReason);

        var unnamed = new RemoteBindingOptions { Enabled = true, Port = 8443 };
        Assert.False(unnamed.TryValidate(out var unnamedReason));
        Assert.Contains("name the interface", unnamedReason!, StringComparison.Ordinal);

        var wellFormed = new RemoteBindingOptions { Enabled = true, Interface = "0.0.0.0", Port = 8443 };
        Assert.False(wellFormed.TryValidate(out var pairingReason));
        Assert.Contains("paired device identity", pairingReason!, StringComparison.Ordinal);
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
