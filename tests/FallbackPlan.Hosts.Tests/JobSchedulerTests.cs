using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The queue's cancellation semantics (ADR-0029 §4, Amendment 4), drilled
/// directly: a queued job WITHOUT <see cref="QueuedJob.OnCancelledBeforeStart"/>
/// keeps the original run-with-cancelled-token path (fan-out and the sweep
/// depend on it — their runners handle their own cancellation); a queued job
/// WITH it is taken out of play at the command; and a job that has already
/// started never takes the callback path. Everything gates on task
/// completions, never on delays, so the drills cannot flake on timing.
/// </summary>
[TestClass]
public sealed class JobSchedulerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueuedJob Job(
        string id, Func<CancellationToken, ValueTask> run, Action? onCancelledBeforeStart = null) =>
        new(id, JobLane.Writer, UserInitiated: false, $"test {id}", run, onCancelledBeforeStart);

    /// <summary>Occupies the writer lane until the returned source is released.</summary>
    private static async Task<TaskCompletionSource> HoldTheLaneAsync(JobScheduler scheduler)
    {
        var started = Signal();
        var release = Signal();
        scheduler.Enqueue(Job("lane-holder", async _ =>
        {
            started.SetResult();
            await release.Task;
        }));
        await started.Task.WaitAsync(Patience);
        return release;
    }

    [TestMethod]
    public async Task Cancel_AQueuedJobWithoutTheCallback_StillRunsWithAnAlreadyCancelledToken()
    {
        await using var scheduler = new JobScheduler();
        var release = await HoldTheLaneAsync(scheduler);

        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Enqueue(Job("plain", token =>
        {
            observed.SetResult(token.IsCancellationRequested);
            return ValueTask.CompletedTask;
        }));

        Assert.IsTrue(scheduler.Cancel("plain"));
        Assert.IsTrue(scheduler.IsActive("plain"), "without the callback, the job stays queued as before");

        release.SetResult();
        Assert.IsTrue(
            await observed.Task.WaitAsync(Patience),
            "the job still runs when the lane drains, and its token is already cancelled");
    }

    [TestMethod]
    public async Task Cancel_AQueuedJobWithTheCallback_IsTakenOutOfPlayAtTheCommand()
    {
        await using var scheduler = new JobScheduler();
        var release = await HoldTheLaneAsync(scheduler);

        var callbacks = 0;
        var ran = Signal();
        scheduler.Enqueue(Job(
            "doomed",
            _ =>
            {
                ran.SetResult();
                return ValueTask.CompletedTask;
            },
            () => Interlocked.Increment(ref callbacks)));

        Assert.IsTrue(scheduler.Cancel("doomed"));
        Assert.AreEqual(1, callbacks, "the callback records the cancellation, once, at the command");
        Assert.IsFalse(scheduler.IsActive("doomed"), "out of play means out of play");
        Assert.IsFalse(scheduler.Cancel("doomed"), "a second cancel is the honest not-found");

        // The tombstoned queue slot consumed only its own semaphore token:
        // the next job through the lane still gets its turn.
        var next = Signal();
        scheduler.Enqueue(Job("next", _ =>
        {
            next.SetResult();
            return ValueTask.CompletedTask;
        }));
        release.SetResult();
        await next.Task.WaitAsync(Patience);

        Assert.IsFalse(ran.Task.IsCompleted, "a job cancelled before it started never runs");
        Assert.AreEqual(1, callbacks);
    }

    [TestMethod]
    public async Task Cancel_AStartedJob_NeverTakesTheCallbackPath()
    {
        await using var scheduler = new JobScheduler();
        var callbacks = 0;
        var started = Signal();
        var observedCancel = Signal();
        scheduler.Enqueue(Job(
            "running",
            async token =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    observedCancel.SetResult();
                }
            },
            () => Interlocked.Increment(ref callbacks)));

        await started.Task.WaitAsync(Patience);
        Assert.IsTrue(scheduler.Cancel("running"));

        await observedCancel.Task.WaitAsync(Patience);
        Assert.AreEqual(0, callbacks, "a started job is stopped through its token, not the callback");
    }
}
