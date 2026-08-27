using System.Collections.Concurrent;
using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// True suspend/resume under priority pressure (ADR-0047's preemption
/// slice): when every writer worker is busy and a higher-priority backup
/// arrives, the lowest-priority running job PAUSES at its next file
/// boundary — its in-memory state held, its worker freed — the incomer
/// runs, and the freed slot resumes the paused run exactly where it
/// stopped. Shutdown degrades a paused run to the ordinary cancelled →
/// re-run path. Establishes FR-SVC-014.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PreemptionTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose() => _timeout.Dispose();

    [TestMethod]
    public async Task AHigherPriorityArrival_PausesTheRunningJob_RunsAndThenResumesIt()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();

        var gate = new PauseGate();
        var lowStarted = Tcs();
        var lowMayFinish = Tcs();
        var lowDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "low", JobLane.Writer, UserInitiated: false, "low-priority run",
            async token =>
            {
                order.Enqueue("low-start");
                lowStarted.SetResult();

                // The file loop: a pause point per iteration, exactly as the
                // capture pipeline checks between scan events.
                for (var i = 0; i < 3; i++)
                {
                    await gate.WaitWhilePausedAsync(token);
                    if (i == 0)
                    {
                        await lowMayFinish.Task.WaitAsync(token);
                    }
                }

                order.Enqueue("low-end");
                lowDone.SetResult();
            },
            Priority: 0,
            PauseGate: gate));

        await lowStarted.Task.WaitAsync(Timeout);

        var highDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "high", JobLane.Writer, UserInitiated: false, "high-priority run",
            token =>
            {
                order.Enqueue("high");
                highDone.SetResult();
                return ValueTask.CompletedTask;
            },
            Priority: 9));

        // The incomer preempts: low parks at its barrier, high runs whole.
        lowMayFinish.SetResult();
        await highDone.Task.WaitAsync(Timeout);
        await lowDone.Task.WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { "low-start", "high", "low-end" }, order.ToArray());
    }

    [TestMethod]
    public async Task ShutdownWhileAJobIsPaused_CancelsItCleanly()
    {
        var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var observedCancel = Tcs();

        queue.Enqueue(new QueuedJob(
            "parked", JobLane.Writer, UserInitiated: false, "will be paused",
            async token =>
            {
                started.SetResult();
                try
                {
                    while (true)
                    {
                        await gate.WaitWhilePausedAsync(token);
                        await Task.Delay(10, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    observedCancel.SetResult();
                    throw;
                }
            },
            Priority: 0,
            PauseGate: gate));
        await started.Task.WaitAsync(Timeout);

        queue.Enqueue(new QueuedJob(
            "pressure", JobLane.Writer, UserInitiated: false, "forces the pause",
            async token => await Task.Delay(50, token),
            Priority: 5));

        // Disposal reaches the parked job through its own token: the paused
        // in-memory state degrades to the ordinary cancelled → re-run path.
        await queue.DisposeAsync();
        await observedCancel.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task ARealBackup_PausedByAHigherPriorityRun_ResumesAndCommitsWhole()
    {
        // End to end through the capture pipeline: the pause gate is checked
        // between scan events, so a many-file source yields thousands of
        // park opportunities while the trivial high-priority job jumps in.
        using var harness = new HostHarness();
        for (var i = 0; i < 1500; i++)
        {
            harness.WriteSourceFile($"many/file-{i:d5}.txt", $"contents of file {i}");
        }

        harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(harness.StateDirectory, "vault"));
        await harness.CreateRepositoryAsync();

        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
                MaxConcurrentBackupsOverride = 1,
            },
            passphrase,
            Timeout);

        var set = runtime.Configuration.BackupSets.Single();
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false);

        // Wait until the capture is genuinely running, then outrank it.
        while (!runtime.Jobs.Jobs.Any(job =>
            job.BackupSetId == set.Id && job.State is JobState.Scanning or JobState.Publishing))
        {
            Assert.IsFalse(backup.IsCompleted, "the backup finished before the test could preempt it");
            await Task.Delay(10, Timeout);
        }

        var highRan = Tcs();
        EnqueueHighPriorityJob(runtime, highRan);

        await highRan.Task.WaitAsync(Timeout);
        Assert.IsFalse(
            backup.IsCompleted,
            "the high-priority job must have run while the backup was suspended, not after it");

        var outcome = await backup.WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        Assert.IsTrue(
            runtime.Jobs.Jobs.Any(job => job.BackupSetId == set.Id && job.State == JobState.Complete));

        static void EnqueueHighPriorityJob(ServiceRuntime runtime, TaskCompletionSource highRan) =>
            runtime.Queue.Enqueue(new QueuedJob(
                "priority-visitor", JobLane.Writer, UserInitiated: false, "outranks the capture",
                token =>
                {
                    highRan.SetResult();
                    return ValueTask.CompletedTask;
                },
                Priority: 50));
    }

    private static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
