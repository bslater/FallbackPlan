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
[TestClass]
public sealed class ServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task While_the_service_runs_no_second_writer_can_take_the_role()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();

        // FR-SVC-002. This is the whole point of the wave: two processes on one
        // state directory are the same writer by construction, and the second
        // must be refused rather than silently drawing from the same sequence
        // space.
        var refused = Assert.ThrowsExactly<ClientStateException>(
            () => StateDirectoryLock.Acquire(_harness.StateDirectory, StateDirectoryLock.DirectRole));

        Assert.Contains(StateDirectoryLock.ServiceRole, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
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

        Assert.IsInstanceOfType<BackupSetsResult>(await client.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var sets);
        Assert.ContainsSingle(sets.Sets);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(await client.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.AreEqual(ContractVersion.Current.ToString(), description.ContractVersion);
        Assert.IsFalse(description.RemoteBindingEnabled);
        Assert.AreEqual(_harness.StateDirectory, description.StateDirectory);
    }

    [TestMethod]
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
        Assert.IsInstanceOfType<JobAcceptedResult>(await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token), out var accepted);
        Assert.IsNotEmpty(accepted.JobId);

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

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        Assert.ContainsSingle(snapshots.Snapshots);

        await _timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watching);
    }

    [TestMethod]
    public async Task Cancelling_a_job_that_is_not_running_says_so_rather_than_pretending()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(new CancelJobCommand("no-such-job"), _timeout.Token), out var error);

        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    [TestMethod]
    public async Task The_service_verifies_the_blobs_it_holds()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // This used to answer "read path, run it directly". The refusal was
        // honest while it stood, but a service that cannot verify its own
        // repository is a service a console cannot ask the only question that
        // matters after a backup.
        Assert.IsInstanceOfType<VerificationResult>(await handler.ExecuteAsync(new VerifyCommand("records"), _timeout.Token), out var verified);

        Assert.IsTrue(verified.ObjectsChecked > 0, "the repository holds blobs, so the sweep must have examined some");
        Assert.AreEqual(0, verified.Failures);
        Assert.AreEqual("records", verified.Level);
    }

    [TestMethod]
    public async Task An_unknown_verify_level_is_refused_with_the_vocabulary()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(new VerifyCommand("paranoid"), _timeout.Token), out var error);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("locator | digest | records", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_healthy_repository_checks_clean()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<CheckResult>(await handler.ExecuteAsync(new CheckCommand("digest"), _timeout.Token), out var check);

        // Findings only, so empty means "nothing is wrong" rather than
        // "nothing ran" — the counts of what was healthy are not findings.
        Assert.IsEmpty(check.Findings);
    }

    [TestMethod]
    public async Task A_restore_plan_says_what_it_would_write_before_anything_moves()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        Assert.IsInstanceOfType<RestorePlanResult>(await handler.ExecuteAsync(new PlanRestoreCommand(snapshot.SnapshotId, null), _timeout.Token), out var plan);

        Assert.AreEqual(2, plan.Files);
        Assert.IsTrue(plan.Bytes > 0);
        Assert.IsEmpty(plan.MissingObjects);
    }

    [TestMethod]
    public async Task A_restore_commanded_through_the_contract_writes_on_the_service_machine()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<RestoreResult>(await handler.ExecuteAsync(
                new RunRestoreCommand(snapshot.SnapshotId, null, destination), _timeout.Token), out var restored);

        // ADR-0028 §6: the output directory is a path on the machine running the
        // service. A caller is told what happened and never sent the files, so
        // the proof of a restore is on this disk rather than in the result.
        Assert.AreEqual(2, restored.Restored);
        Assert.AreEqual(0, restored.Failed);

        // Quarantine by default (FR-RST-006): the content lands under the
        // commanded directory rather than directly in it, and the result says
        // where — a caller told the commanded path could not find its files.
        Assert.StartsWith(destination, restored.OutputDirectory, StringComparison.Ordinal);
        Assert.AreNotEqual(destination, restored.OutputDirectory);

        Assert.AreEqual(
            "hello",
            await File.ReadAllTextAsync(Path.Combine(restored.OutputDirectory, "notes.txt"), _timeout.Token));
        Assert.AreEqual(
            "deeper",
            await File.ReadAllTextAsync(
                Path.Combine(restored.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task A_restore_of_a_snapshot_nobody_has_is_not_found_rather_than_empty()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(
                new PlanRestoreCommand(new string('a', 32), null), _timeout.Token), out var error);

        // An empty plan and an absent snapshot are different answers, and a
        // caller that cannot tell them apart cannot report either honestly.
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    [TestMethod]
    public async Task The_service_reports_status_derived_in_one_place()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");
        await _harness.BackUpAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);

        // The client receives the derivation, never the inputs to redo it
        // (10 §3.1) — a front end that computed its own would be a second
        // implementation of the never-merge rules.
        var set = Assert.ContainsSingle(status.Sets);
        Assert.IsNotNull(set.NextRun);
        Assert.AreEqual(Environment.MachineName, status.MachineName);
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
