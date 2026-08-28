using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// One run per set at a time, whichever door the run came through (FR-SVC-012;
/// ADR-0027 §1). The scheduled pass has always checked before queueing; these
/// pin the doors that historically did not — the manual trigger and the
/// upsert's first backup — because two runs of one set share a spool
/// directory, and the second run's crash-hygiene sweep deletes the first
/// run's live spool out from under it (a sharing violation on Windows,
/// silent data destruction where the delete wins). The FR-SVC-015 half —
/// that the upsert still queues a first backup at all — stays established
/// by SetChangeTests; here only its collision with a manual trigger.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BackupConcurrencyTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task RunBackup_WhileTheSetsBackupIsStillQueued_AttachesToTheActiveJob()
    {
        await using var runtime = await StartAsync();
        var (firstWorker, releaseFirst) = Occupy(runtime, JobLane.Writer);
        var (secondWorker, releaseSecond) = Occupy(runtime, JobLane.Writer);

        // Both pool workers are parked, so the first command's capture is
        // still queued — beyond doubt — when the second command arrives.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), Timeout), out var first);
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), Timeout), out var second);

        Assert.AreEqual(
            first.JobId,
            second.JobId,
            "a manual trigger for a set whose run is still queued must answer with that run, not begin another");

        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual(
            1,
            runtime.Jobs.Jobs.Count(job => job.BackupSetId == set.Id),
            "the second command must not have begun a second journal row for the set");

        releaseFirst();
        releaseSecond();
        await firstWorker.WaitAsync(Timeout);
        await secondWorker.WaitAsync(Timeout);
        await WaitForSettledAsync(runtime, set.Id);
    }

    [TestMethod]
    public async Task UpsertBackupSet_TheFirstBackupItQueues_AbsorbsAnImmediateManualTrigger()
    {
        // The reported field failure: save a new set (its first backup queues
        // by itself, FR-SVC-015), then click "Back up now" on the freshly
        // rendered card. The click must land on the queued first backup.
        await using var runtime = await StartAsync();
        var (firstWorker, releaseFirst) = Occupy(runtime, JobLane.Writer);
        var (secondWorker, releaseSecond) = Occupy(runtime, JobLane.Writer);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var freshId = new string('d', 32);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(
                new UpsertBackupSetCommand(new BackupSetDescriptor(
                    freshId, "fresh", _harness.SourceRoot, null, [], [], ["vault"])),
                Timeout));

        var queued = Scheduler.LatestJobFor(runtime, freshId);
        Assert.IsNotNull(queued, "saving a new set queues its first backup (FR-SVC-015)");

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("fresh", Full: false), Timeout), out var manual);
        Assert.AreEqual(
            queued,
            manual.JobId,
            "the manual trigger must attach to the auto-queued first backup, not race it");
        Assert.AreEqual(
            1,
            runtime.Jobs.Jobs.Count(job => job.BackupSetId == freshId),
            "one journal row: the first backup, with no duplicate begun by the trigger");

        releaseFirst();
        releaseSecond();
        await firstWorker.WaitAsync(Timeout);
        await secondWorker.WaitAsync(Timeout);
        await WaitForSettledAsync(runtime, freshId);
    }

    [TestMethod]
    public async Task Enqueue_ConcurrentCallsForOneSet_RunExactlyOneCapture()
    {
        // The guard must hold under simultaneity, not just in sequence: the
        // check and the begin have to be one atomic step, or two commands
        // arriving together both pass the check and both begin.
        await using var runtime = await StartAsync();
        var (firstWorker, releaseFirst) = Occupy(runtime, JobLane.Writer);
        var (secondWorker, releaseSecond) = Occupy(runtime, JobLane.Writer);

        var set = runtime.Configuration.BackupSets.Single();
        var t0 = DateTimeOffset.Now;
        var completions = new System.Collections.Concurrent.ConcurrentBag<Task<BackupOutcome>>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => completions.Add(Scheduler.Enqueue(runtime, set, t0, userInitiated: true)))));

        Assert.AreEqual(
            1,
            runtime.Jobs.Jobs.Count(job => job.BackupSetId == set.Id),
            "eight simultaneous enqueues for one set must begin exactly one job");

        releaseFirst();
        releaseSecond();
        await firstWorker.WaitAsync(Timeout);
        await secondWorker.WaitAsync(Timeout);

        var outcomes = await Task.WhenAll(completions.Select(completion => completion.WaitAsync(Timeout)));
        Assert.ContainsSingle(outcomes.Where(outcome => outcome.Outcome == "ran"));
        Assert.HasCount(7, outcomes.Where(outcome => outcome.Outcome == "already-running").ToList());
    }

    /// <summary>
    /// Parks one lane worker until released, honouring cancellation so
    /// disposal never hangs behind it.
    /// </summary>
    private static (Task Done, Action Release) Occupy(ServiceRuntime runtime, JobLane lane)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runtime.Queue.Enqueue(new QueuedJob(
            "occupy-" + Guid.NewGuid().ToString("n"),
            lane,
            UserInitiated: true,
            $"occupy the {lane} lane",
            async cancellationToken =>
            {
                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown reached the blocker first; that is release enough.
                }

                done.TrySetResult();
            }));

        return (done.Task, () => release.TrySetResult());
    }

    private async Task WaitForSettledAsync(ServiceRuntime runtime, string setId)
    {
        while (runtime.Jobs.Jobs.Any(job => job.BackupSetId == setId && job.State is not (
            Domain.Jobs.JobState.Complete
            or Domain.Jobs.JobState.CompletedWithFailures
            or Domain.Jobs.JobState.Cancelled
            or Domain.Jobs.JobState.FailedRecoverable
            or Domain.Jobs.JobState.FailedPermanent)))
        {
            await Task.Delay(10, Timeout);
        }
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "bytes for the capture to carry");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            Timeout);
    }
}
