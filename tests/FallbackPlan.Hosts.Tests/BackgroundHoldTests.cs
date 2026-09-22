using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The writer pool's background hold (NFR-PERF-013, ADR-0069): the mechanism a
/// shut background window asks for, at the scheduler level where every
/// interleaving is controllable and no capture stands between the assertion
/// and what it is about.
/// </summary>
/// <remarks>
/// <para>
/// The hold is a <em>standing state of the pool</em> and not a per-job ask,
/// and that is the whole design. ADR-0047's preemption resumes the
/// best-ranked parked run the moment a writer worker frees — which is
/// precisely what the park itself does — so a one-shot
/// <see cref="PauseGate.Pause"/> would be undone within milliseconds by the
/// worker the park released. While the hold stands the pump neither resumes a
/// parked background run nor starts a queued one.
/// </para>
/// <para>
/// What the hold does <em>not</em> do is bound how long a run may stay parked.
/// That is ADR-0047's max-pause cap, unchanged and reused: a closure that
/// outlasts it self-cancels the run into the interruption-safe re-run path,
/// which is exactly right for a window that stays shut. The case below asserts
/// it rather than assuming the reuse.
/// </para>
/// <para>
/// Does not establish FR-SVC-014 — the preemption surface this borrows is
/// established by <see cref="JobSchedulerPreemptionTests"/>.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BackgroundHoldTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    /// <summary>The pass's words, which the journal and the progress surface carry.</summary>
    private static string Shut => "held outside the background window 22:00-06:00";

    public void Dispose() => _timeout.Dispose();

    [TestMethod]
    public async Task ARunningBackgroundJob_ParksUnderTheHold()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var mayStop = Tcs();
        var finished = Tcs();

        queue.Enqueue(Background("capture", gate, started, mayStop, finished));
        await started.Task.WaitAsync(Timeout);

        var parked = await queue.HoldBackgroundAsync(Shut, Timeout);

        Assert.AreEqual(1, parked);
        Assert.IsTrue(gate.IsPaused);
        Assert.IsTrue(gate.Parked.IsCompleted);

        mayStop.SetResult();
        queue.ReleaseBackground();
        await finished.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AHeldRun_StaysParked_ThoughAWorkerIsFree()
    {
        // The case the design turns on. The pool is one worker wide and the
        // park handed that worker back, so ADR-0047's resume-on-free-slot
        // path would pick this run up at once — which is what a one-shot
        // pause would get. The hold has to stand.
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var mayStop = Tcs();
        var finished = Tcs();

        queue.Enqueue(Background("capture", gate, started, mayStop, finished));
        await started.Task.WaitAsync(Timeout);
        Assert.AreEqual(1, await queue.HoldBackgroundAsync(Shut, Timeout));

        // The job is told it may stop, so the only thing between it and
        // finishing is the resume the hold must not grant.
        mayStop.SetResult();

        // Long enough for a freed worker to have gone round the pump several
        // times: a resume would be immediate, not eventual.
        await Task.Delay(250, Timeout);

        Assert.IsTrue(gate.IsPaused, "the hold stands, so the run must still be parked");
        Assert.IsTrue(gate.Parked.IsCompleted, "a resume would have re-armed the park signal");
        Assert.IsFalse(finished.Task.IsCompleted);

        queue.ReleaseBackground();
        await finished.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AQueuedBackgroundJob_DoesNotStartUnderTheHold()
    {
        // With the default pool width of one, a pass that queued three sets
        // and then found the window shut would park the running one and hand
        // its worker straight to the next — half a limit, and the half an
        // operator would notice.
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var mayStop = Tcs();
        var finished = Tcs();
        queue.Enqueue(Background("running", gate, started, mayStop, finished));
        await started.Task.WaitAsync(Timeout);

        await queue.HoldBackgroundAsync(Shut, Timeout);
        mayStop.SetResult();

        var nextStarted = Tcs();
        var nextFinished = Tcs();
        queue.Enqueue(new QueuedJob(
            "queued", JobLane.Writer, UserInitiated: false, "waits out the window",
            async token =>
            {
                nextStarted.SetResult();

                // Token-aware, so that a case which fails its assertion still
                // lets the queue dispose: a job parked on a signal nobody is
                // left to set would turn a clean red into a hung suite.
                await nextFinished.Task.WaitAsync(token);
            }));

        await Task.Delay(250, Timeout);
        Assert.IsFalse(nextStarted.Task.IsCompleted, "a queued background run must not start under the hold");

        queue.ReleaseBackground();
        await finished.Task.WaitAsync(Timeout);
        await nextStarted.Task.WaitAsync(Timeout);
        nextFinished.SetResult();
    }

    [TestMethod]
    public async Task AUserInitiatedRun_IsNeitherAskedNorHeld()
    {
        // ADR-0029's standing rule, which the window must not touch: a person
        // is never held by it. A restore at noon, a manual run, a console
        // "Back up now" — none of them is background activity.
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var mayFinish = Tcs();
        queue.Enqueue(new QueuedJob(
            "person", JobLane.Writer, UserInitiated: true, "a person is waiting",
            async token =>
            {
                started.SetResult();
                while (!mayFinish.Task.IsCompleted)
                {
                    await gate.WaitWhilePausedAsync(token);
                    await Task.Delay(5, token);
                }
            },
            PauseGate: gate));
        await started.Task.WaitAsync(Timeout);

        Assert.AreEqual(0, await queue.HoldBackgroundAsync(Shut, Timeout));
        Assert.IsFalse(gate.IsPaused, "a user-initiated run is not asked to park");

        // And one queued while the hold stands starts at once.
        var visitorRan = Tcs();
        queue.Enqueue(new QueuedJob(
            "visitor", JobLane.Writer, UserInitiated: true, "jumps the held queue",
            _ =>
            {
                visitorRan.SetResult();
                return ValueTask.CompletedTask;
            }));

        mayFinish.SetResult();
        await visitorRan.Task.WaitAsync(Timeout);
        queue.ReleaseBackground();
    }

    [TestMethod]
    public async Task AHeldRunPastTheMaxPauseAge_SelfCancelsToTheReRunPath()
    {
        // The keystone, asserted rather than assumed: a window that stays shut
        // for longer than a parked run may hold its memory and its live write
        // intent hands that run to the interruption-safe re-run path, with no
        // machinery the window had to add. The hold is still standing when it
        // happens — the run is not cancelled BY the release.
        await using var queue = new JobScheduler(writerWorkers: 1, maxPause: TimeSpan.FromMilliseconds(250));
        var gate = new PauseGate();
        var started = Tcs();
        var cancelled = Tcs();

        queue.Enqueue(new QueuedJob(
            "outstays", JobLane.Writer, UserInitiated: false, "parks for longer than the cap allows",
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
                    cancelled.SetResult();
                    throw;
                }
            },
            PauseGate: gate));
        await started.Task.WaitAsync(Timeout);
        await queue.HoldBackgroundAsync(Shut, Timeout);

        await cancelled.Task.WaitAsync(Timeout);
        await WaitForAsync(() => !queue.IsActive("outstays"), Timeout);
    }

    [TestMethod]
    public async Task ARunAlreadyParkedByAPreemption_IsNotResumedUnderTheHold()
    {
        // The two mechanisms share one paused set, so they have to compose:
        // a run parked because a higher-priority job outranked it must not be
        // resumed by the freeing of that job's slot while the window is shut.
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var mayStop = Tcs();
        var finished = Tcs();
        queue.Enqueue(Background("victim", gate, started, mayStop, finished));
        await started.Task.WaitAsync(Timeout);

        var visitorRan = Tcs();
        var releaseVisitor = Tcs();
        queue.Enqueue(new QueuedJob(
            "visitor", JobLane.Writer, UserInitiated: false, "outranks the capture",
            async token =>
            {
                visitorRan.SetResult();
                await releaseVisitor.Task.WaitAsync(token);
            },
            Priority: 50));
        await visitorRan.Task.WaitAsync(Timeout);
        await WaitForAsync(() => gate.Parked.IsCompleted, Timeout);

        // The window shuts over an already-parked run: nothing new to ask,
        // and the count is of what is parked rather than of what this call
        // asked for — the run is held by the window from here whoever
        // originally suspended it.
        mayStop.SetResult();
        Assert.AreEqual(1, await queue.HoldBackgroundAsync(Shut, Timeout));

        releaseVisitor.SetResult();
        await Task.Delay(250, Timeout);
        Assert.IsFalse(finished.Task.IsCompleted, "the freed slot must not resume a run the window is holding");

        queue.ReleaseBackground();
        await finished.Task.WaitAsync(Timeout);
    }

    /// <summary>
    /// A background writer job that checks in at a pause gate every few
    /// milliseconds, the way a capture checks in between scan events.
    /// <paramref name="mayStop"/> is the test's leave to end and
    /// <paramref name="finished"/> is the job's own word that it did — two
    /// signals, because a job that has been told it may stop and has not been
    /// resumed to notice is exactly the state these cases are about.
    /// </summary>
    private static QueuedJob Background(
        string jobId,
        PauseGate gate,
        TaskCompletionSource started,
        TaskCompletionSource mayStop,
        TaskCompletionSource finished) =>
        new(
            jobId, JobLane.Writer, UserInitiated: false, $"background {jobId}",
            async token =>
            {
                started.SetResult();
                while (!mayStop.Task.IsCompleted)
                {
                    await gate.WaitWhilePausedAsync(token);
                    await Task.Delay(5, token);
                }

                finished.SetResult();
            },
            PauseGate: gate);

    private static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
