using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The queue's cancellation semantics (ADR-0029 §4, Amendment 5; FR-SVC-017),
/// drilled directly: a queued job WITHOUT <see cref="QueuedJob.OnCancelledBeforeStart"/>
/// keeps the original run-with-cancelled-token path (fan-out and the sweep
/// depend on it — their runners handle their own cancellation); a queued job
/// WITH it is taken out of play at the command; and a job that has already
/// started never takes the callback path. And settlement (Amendment 6): a
/// job's <see cref="QueuedJob.OnSettled"/> runs only once its identity is
/// free again, however its run ended, so whoever it answers can ask for the
/// same job at once. A stopping queue (Amendment 7) answers each job it never
/// started through the same callback, and no job it started; a job may carry
/// a callback of its own for the stop (Amendment 8), which the stop prefers and
/// a cancel command never calls. Everything gates on task completions, never
/// on delays, so the drills cannot flake on timing.
/// </summary>
[TestClass]
public sealed class JobSchedulerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueuedJob Job(
        string id, Func<CancellationToken, ValueTask> run, Action? onCancelledBeforeStart = null) =>
        new(id, JobLane.Writer, UserInitiated: false, $"test {id}", run, OnCancelledBeforeStart: onCancelledBeforeStart);

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

        // The removed queue entry leaves its semaphore token behind, and a
        // worker that wakes to an empty lane just waits again: the next job
        // through the lane still gets its turn.
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

    [TestMethod]
    public async Task Stopping_AnswersEachJobItNeverStarted_ThroughItsCallback_AndNoJobItStarted()
    {
        // The queue runs nothing it had not started by the time it stops. A
        // job that records its own cancellation is told so there, as a cancel
        // before it started would tell it, or whoever waits on it waits for
        // ever. On every lane: the job holding each had started, and is
        // stopped through its token.
        await using var scheduler = new JobScheduler();
        var lanes = new[] { JobLane.Writer, JobLane.Reader, JobLane.Transfer };
        var holderCallbacks = 0;
        var callbacks = new int[lanes.Length];
        var ran = 0;
        for (var index = 0; index < lanes.Length; index++)
        {
            var lane = lanes[index];
            var holding = Signal();
            Assert.IsTrue(scheduler.Enqueue(new QueuedJob(
                $"holder-{lane}", lane, UserInitiated: false, $"hold {lane}",
                async token =>
                {
                    holding.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                OnCancelledBeforeStart: () => Interlocked.Increment(ref holderCallbacks))));
            await holding.Task.WaitAsync(Patience);

            var slot = index;
            Assert.IsTrue(scheduler.Enqueue(new QueuedJob(
                $"never-started-{lane}", lane, UserInitiated: false, $"never started {lane}",
                _ =>
                {
                    Interlocked.Increment(ref ran);
                    return ValueTask.CompletedTask;
                },
                OnCancelledBeforeStart: () => Interlocked.Increment(ref callbacks[slot]))));
        }

        await scheduler.DisposeAsync().AsTask().WaitAsync(Patience);

        for (var index = 0; index < lanes.Length; index++)
        {
            Assert.AreEqual(1, callbacks[index], $"the {lanes[index]} lane's job the queue never started was not answered once");
        }

        Assert.AreEqual(0, ran, "the stopping queue ran a job it had not started");
        Assert.AreEqual(0, holderCallbacks, "a job the queue had started was answered as one it never started");
    }

    [TestMethod]
    public async Task Stopping_AJobWhoseCallbackThrows_IsTheJobsFault_AndTheRestAreStillAnswered()
    {
        // The stop answers what it never started from inside its own
        // disposal, where nobody is left to handle an exception: one job's
        // callback failing must neither escape the stop nor leave the jobs
        // queued behind it unanswered.
        await using var scheduler = new JobScheduler();
        var holding = Signal();
        scheduler.Enqueue(Job("lane-holder", async token =>
        {
            holding.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        await holding.Task.WaitAsync(Patience);

        var answered = 0;
        scheduler.Enqueue(Job("faulting", _ => ValueTask.CompletedTask, () => throw new InvalidOperationException("the callback's own failure")));
        scheduler.Enqueue(Job("behind", _ => ValueTask.CompletedTask, () => Interlocked.Increment(ref answered)));

        await scheduler.DisposeAsync().AsTask().WaitAsync(Patience);

        Assert.AreEqual(1, answered, "a job queued behind one whose callback threw was not answered");
    }

    [TestMethod]
    public async Task Stopping_AnswersAJobItNeverStarted_ThroughItsStopCallback_RatherThanItsCancelOne()
    {
        // A job can be answered at the stop without recording a cancellation
        // it was never given (Amendment 8): the stop's own callback, where the
        // job has one, is the one the stop calls.
        await using var scheduler = new JobScheduler();
        var holding = Signal();
        scheduler.Enqueue(Job("lane-holder", async token =>
        {
            holding.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        await holding.Task.WaitAsync(Patience);

        var stopped = 0;
        var cancelled = 0;
        scheduler.Enqueue(new QueuedJob(
            "never-started", JobLane.Writer, UserInitiated: false, "test never-started", _ => ValueTask.CompletedTask,
            OnCancelledBeforeStart: () => Interlocked.Increment(ref cancelled),
            OnStoppedBeforeStart: () => Interlocked.Increment(ref stopped)));

        await scheduler.DisposeAsync().AsTask().WaitAsync(Patience);

        Assert.AreEqual(1, stopped, "the stop did not answer the job through its own callback once");
        Assert.AreEqual(0, cancelled, "the stop answered the job as one cancelled before it started");
    }

    [TestMethod]
    public async Task Cancel_AQueuedJobWithOnlyTheStopsCallback_KeepsTheCooperativePath()
    {
        // The stop's callback is the stop's alone: a cancel command leaves a
        // job carrying only that one queued, to run with a token already
        // cancelled, as fan-out relies on (Amendment 5).
        await using var scheduler = new JobScheduler();
        var release = await HoldTheLaneAsync(scheduler);

        var stopped = 0;
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Enqueue(new QueuedJob(
            "cooperative", JobLane.Writer, UserInitiated: false, "test cooperative",
            token =>
            {
                observed.SetResult(token.IsCancellationRequested);
                return ValueTask.CompletedTask;
            },
            OnStoppedBeforeStart: () => Interlocked.Increment(ref stopped)));

        Assert.IsTrue(scheduler.Cancel("cooperative"));
        Assert.IsTrue(scheduler.IsActive("cooperative"), "the cancel took the job out of the queue");

        release.SetResult();
        Assert.IsTrue(await observed.Task.WaitAsync(Patience), "the job did not run with its token already cancelled");
        Assert.AreEqual(0, stopped, "a cancel command called the stop's callback");
    }

    [TestMethod]
    public async Task Settled_IsCalledOnceTheIdentityIsFree_SoTheSameJobCanBeAskedForAtOnce()
    {
        // A run ending is not the job leaving: the queue releases the
        // identity after the run returns, so a caller told from inside the
        // run that the work is done could ask for the same identity again
        // before then, and be coalesced into the run that had just finished.
        // A pair's sync asked for again the moment the last one answered is
        // exactly that. The settled callback is where the identity is free,
        // on every lane.
        await using var scheduler = new JobScheduler();
        foreach (var lane in new[] { JobLane.Transfer, JobLane.Reader, JobLane.Writer })
        {
            var id = $"pair-{lane}";
            var askedAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(scheduler.Enqueue(new QueuedJob(
                id, lane, UserInitiated: false, $"test {id}", _ => ValueTask.CompletedTask,
                OnSettled: () => askedAgain.SetResult(scheduler.Enqueue(
                    new QueuedJob(id, lane, UserInitiated: false, $"again {id}", _ => ValueTask.CompletedTask))))));

            Assert.IsTrue(
                await askedAgain.Task.WaitAsync(Patience),
                $"the {lane} lane still held {id} when it said the job had settled");
        }
    }

    [TestMethod]
    public async Task Settled_IsCalledForARunThatThrew()
    {
        // Whoever waits on a job is answered however its run ended, or a run
        // that failed would leave them waiting for ever.
        await using var scheduler = new JobScheduler();
        foreach (var lane in new[] { JobLane.Transfer, JobLane.Writer })
        {
            var id = $"faulting-{lane}";
            var settled = Signal();
            scheduler.Enqueue(new QueuedJob(
                id, lane, UserInitiated: false, $"test {id}",
                _ => throw new InvalidOperationException("the run's own failure"),
                OnSettled: () => settled.SetResult()));

            await settled.Task.WaitAsync(Patience);
            Assert.IsFalse(scheduler.IsActive(id));
        }
    }
}
