using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service (ADR-0028): sole writer role, a command surface, real job
/// states, and cancellation that lands in the journal.
/// </summary>
public sealed class ServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [Fact]
    public async Task While_the_service_runs_no_second_writer_can_take_the_role()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();

        // FR-SVC-002. This is the whole point of the wave: two processes on one
        // state directory are the same writer by construction, and the second
        // must be refused rather than silently drawing from the same sequence
        // space.
        var refused = Assert.Throws<ClientStateException>(
            () => StateDirectoryLock.Acquire(_harness.StateDirectory, StateDirectoryLock.DirectRole));

        Assert.Contains(StateDirectoryLock.ServiceRole, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_commands_the_service_over_the_local_binding()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        await using var client = await LocalServiceClient.ConnectAsync(
            _harness.StateDirectory, "test", _timeout.Token);

        var sets = Assert.IsType<BackupSetsResult>(
            await client.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token));
        Assert.Single(sets.Sets);

        var description = Assert.IsType<ServiceDescriptionResult>(
            await client.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token));
        Assert.Equal(ContractVersion.Current.ToString(), description.ContractVersion);
        Assert.False(description.RemoteBindingEnabled);
        Assert.Equal(_harness.StateDirectory, description.StateDirectory);
    }

    [Fact]
    public async Task A_backup_commanded_by_a_client_runs_and_reports_states_beyond_scanning()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", new string('x', 200_000));
        _harness.WriteSourceFile("deep/more.txt", new string('y', 200_000));
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var seen = new List<JobState>();

        // Subscribed here, on this thread, before the backup is commanded.
        // Calling WatchAsync inside the Task.Run below would leave the
        // subscription to whenever the pool got round to it, and a busy pool is
        // exactly when the first states would be missed.
        var progressEvents = runtime.Progress.WatchAsync(_timeout.Token);

        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in progressEvents)
                {
                    lock (seen)
                    {
                        seen.Add(progress.Progress.State);
                    }
                }
            },
            _timeout.Token);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var accepted = Assert.IsType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token));
        Assert.NotEmpty(accepted.JobId);

        // Wait for the watcher, not for the job. The job reaching Complete says
        // the engine finished; it says nothing about whether the task draining
        // the progress channel has caught up, and reading `seen` before it has
        // is a second race distinct from the subscription one — subscribing
        // eagerly guarantees no event is missed, not that every event has been
        // observed yet. The channel is FIFO, so a watcher that has seen
        // Complete has seen everything before it.
        await WaitForAsync(() =>
        {
            lock (seen)
            {
                return seen.Contains(JobState.Complete);
            }
        });

        // FR-SVC-006 and ADR-0029 §5: before this, eight of fourteen states were
        // written nowhere, and a ten-hour backup announced `Scanning` and then
        // nothing at all.
        List<JobState> states;
        lock (seen)
        {
            states = [.. seen];
        }

        Assert.Contains(JobState.Scanning, states);
        Assert.Contains(JobState.Packing, states);
        Assert.Contains(JobState.Publishing, states);
        Assert.Contains(JobState.Complete, states);

        var snapshots = Assert.IsType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token));
        Assert.Single(snapshots.Snapshots);

        await _timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task Cancelling_a_job_that_is_not_running_says_so_rather_than_pretending()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var error = Assert.IsType<ServiceError>(
            await handler.ExecuteAsync(new CancelJobCommand("no-such-job"), _timeout.Token));

        Assert.Equal(ServiceErrorReason.NotFound, error.Reason);
    }

    [Fact]
    public async Task A_read_path_the_service_does_not_serve_is_named_not_silently_missing()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var error = Assert.IsType<ServiceError>(
            await handler.ExecuteAsync(new VerifyCommand("digest"), _timeout.Token));

        Assert.Equal(ServiceErrorReason.Unavailable, error.Reason);
        Assert.Contains("read path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_reports_status_derived_in_one_place()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");
        await _harness.BackUpAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var status = Assert.IsType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token));

        // The client receives the derivation, never the inputs to redo it
        // (10 §3.1) — a front end that computed its own would be a second
        // implementation of the never-merge rules.
        var set = Assert.Single(status.Sets);
        Assert.NotNull(set.NextRun);
        Assert.Equal(Environment.MachineName, status.MachineName);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                RepositoryPath = _harness.RepositoryPath,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }
}
