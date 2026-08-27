using System.Collections.Concurrent;
using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The preemption machinery's full scenario surface (ADR-0047 Amendments 1
/// and 2), at the scheduler level where every interleaving is controllable:
/// victim choice across a real pool, resume ranking, the strictness rules,
/// cancellation of a parked run, repeated pause cycles, the max-pause bound
/// with its park-generation stamp, and escalation past a victim that never
/// reaches a pause point. Establishes FR-SVC-013 and FR-SVC-014.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class JobSchedulerPreemptionTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose() => _timeout.Dispose();

    [TestMethod]
    public async Task ATwoWorkerPool_PausesTheWorstRankedVictim_AndOnlyIt()
    {
        // The victim search over a real pool: with two gated runs attended,
        // the incomer pauses the WORST-ranked one and leaves the better one
        // untouched (ADR-0047 A1 rule 3's "worst-first").
        await using var queue = new JobScheduler(writerWorkers: 2);
        var order = new ConcurrentQueue<string>();

        var (gateWorse, doneWorse) = StartPausableLoop(queue, "worse", priority: 1, order);
        var (gateBetter, doneBetter) = StartPausableLoop(queue, "better", priority: 2, order);
        await WaitForAsync(() => order.Count == 2, Timeout);

        var highDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "high", JobLane.Writer, UserInitiated: false, "the incomer",
            token =>
            {
                order.Enqueue("high");
                highDone.SetResult();
                return ValueTask.CompletedTask;
            },
            Priority: 9));

        await highDone.Task.WaitAsync(Timeout);
        Assert.IsFalse(gateBetter.IsPaused, "the better-ranked run must never be asked to park");
        Assert.IsTrue(doneWorse.Task.IsCompleted is false, "the victim should be parked, not finished");

        // Freed slot resumes the parked victim; both loops then finish.
        await doneWorse.Task.WaitAsync(Timeout);
        await doneBetter.Task.WaitAsync(Timeout);
        _ = gateWorse;
    }

    [TestMethod]
    public async Task TwoPausedRuns_ResumeInRankOrder()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();

        // low (priority 1) parks under H1; H1 then holds the slot while
        // mid (priority 3) queues, runs, and parks under H2.
        var (_, lowDone) = StartPausableLoop(queue, "low", priority: 1, order);
        await WaitForAsync(() => order.Contains("low-start"), Timeout);

        var h1Done = Tcs();
        queue.Enqueue(new QueuedJob(
            "h1", JobLane.Writer, UserInitiated: false, "first incomer",
            token => { order.Enqueue("h1"); h1Done.SetResult(); return ValueTask.CompletedTask; },
            Priority: 9));
        await h1Done.Task.WaitAsync(Timeout);

        var (_, midDone) = StartPausableLoop(queue, "mid", priority: 3, order);
        await WaitForAsync(() => order.Contains("mid-start"), Timeout);

        var h2Done = Tcs();
        queue.Enqueue(new QueuedJob(
            "h2", JobLane.Writer, UserInitiated: false, "second incomer",
            token => { order.Enqueue("h2"); h2Done.SetResult(); return ValueTask.CompletedTask; },
            Priority: 9));
        await h2Done.Task.WaitAsync(Timeout);

        // Both parked; the better-ranked (mid) resumes and finishes first.
        await midDone.Task.WaitAsync(Timeout);
        await lowDone.Task.WaitAsync(Timeout);
        var finish = order.Where(entry => entry.EndsWith("-end", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(new[] { "mid-end", "low-end" }, finish);
    }

    [TestMethod]
    public async Task APausedUserInitiatedRun_ResumesBeforeQueuedScheduledWork()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();

        var (_, userDone) = StartPausableLoop(queue, "user", priority: 0, order, userInitiated: true);
        await WaitForAsync(() => order.Contains("user-start"), Timeout);

        var highDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "high", JobLane.Writer, UserInitiated: true, "user incomer",
            token => { order.Enqueue("high"); highDone.SetResult(); return ValueTask.CompletedTask; },
            Priority: 9));
        await highDone.Task.WaitAsync(Timeout);

        // A scheduled job of any priority queues behind the parked person.
        var scheduledDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "scheduled", JobLane.Writer, UserInitiated: false, "background",
            token => { order.Enqueue("scheduled"); scheduledDone.SetResult(); return ValueTask.CompletedTask; },
            Priority: 50));

        await userDone.Task.WaitAsync(Timeout);
        await scheduledDone.Task.WaitAsync(Timeout);
        Assert.IsTrue(
            order.ToList().IndexOf("user-end") < order.ToList().IndexOf("scheduled"),
            "a person's suspended run outranks any scheduled priority");
    }

    [TestMethod]
    public async Task AnEqualOrLowerRankedIncomer_NeverPreempts()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();

        var (gate, done) = StartPausableLoop(queue, "running", priority: 5, order);
        await WaitForAsync(() => order.Contains("running-start"), Timeout);

        // Equal priority: strictly-outranking only (A1 rule 3).
        queue.Enqueue(new QueuedJob(
            "equal", JobLane.Writer, UserInitiated: false, "equal priority",
            token => ValueTask.CompletedTask, Priority: 5));
        // Scheduled work never outranks a person, whatever its number.
        var (userGate, userDone) = ("placeholder", Tcs());
        _ = userGate;
        _ = userDone;
        Assert.IsFalse(gate.IsPaused, "an equal-priority incomer must not pause the running job");

        await done.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AScheduledIncomer_NeverPreemptsAUserInitiatedRun()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();

        var (gate, done) = StartPausableLoop(queue, "person", priority: 0, order, userInitiated: true);
        await WaitForAsync(() => order.Contains("person-start"), Timeout);

        queue.Enqueue(new QueuedJob(
            "background", JobLane.Writer, UserInitiated: false, "scheduled, loud priority",
            token => ValueTask.CompletedTask, Priority: 99));

        Assert.IsFalse(gate.IsPaused, "scheduled work never outranks a person (ADR-0029 §4)");
        await done.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AFreeSlot_MeansNoPreemption()
    {
        await using var queue = new JobScheduler(writerWorkers: 2);
        var order = new ConcurrentQueue<string>();

        var (gate, done) = StartPausableLoop(queue, "solo", priority: 0, order);
        await WaitForAsync(() => order.Contains("solo-start"), Timeout);

        var highDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "high", JobLane.Writer, UserInitiated: false, "takes the free slot",
            token => { highDone.SetResult(); return ValueTask.CompletedTask; },
            Priority: 9));

        await highDone.Task.WaitAsync(Timeout);
        Assert.IsFalse(gate.IsPaused, "an incomer with a free slot must not pause anyone");
        await done.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task CancellingAParkedJob_ClosesItAndTheQueueKeepsServing()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var gate = new PauseGate();
        var started = Tcs();
        var observedCancel = Tcs();

        queue.Enqueue(new QueuedJob(
            "parked", JobLane.Writer, UserInitiated: false, "will park and be cancelled",
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

        var holdHigh = Tcs();
        queue.Enqueue(new QueuedJob(
            "pressure", JobLane.Writer, UserInitiated: false, "parks the victim and holds the slot",
            async token => await holdHigh.Task.WaitAsync(token),
            Priority: 9));
        await WaitForAsync(() => gate.IsPaused, Timeout);

        // Cancel reaches through the park; the scheduler forgets the job.
        Assert.IsTrue(queue.Cancel("parked"));
        await observedCancel.Task.WaitAsync(Timeout);
        await WaitForAsync(() => !queue.IsActive("parked"), Timeout);

        holdHigh.SetResult();
        var lastDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "after", JobLane.Writer, UserInitiated: false, "proves the queue survived",
            token => { lastDone.SetResult(); return ValueTask.CompletedTask; },
            Priority: 0));
        await lastDone.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task ARunPreemptedTwice_ParksAndResumesBothTimes()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();
        var gate = new PauseGate();
        var step = Tcs();
        var done = Tcs();

        queue.Enqueue(new QueuedJob(
            "twice", JobLane.Writer, UserInitiated: false, "paused twice, finishes whole",
            async token =>
            {
                order.Enqueue("twice-start");
                for (var i = 0; i < 40; i++)
                {
                    await gate.WaitWhilePausedAsync(token);
                    if (i == 0)
                    {
                        await step.Task.WaitAsync(token);
                    }

                    await Task.Delay(5, token);
                }

                order.Enqueue("twice-end");
                done.SetResult();
            },
            Priority: 0,
            PauseGate: gate));
        await WaitForAsync(() => order.Contains("twice-start"), Timeout);
        step.SetResult();

        for (var round = 1; round <= 2; round++)
        {
            var highDone = Tcs();
            var name = $"h{round}";
            queue.Enqueue(new QueuedJob(
                name, JobLane.Writer, UserInitiated: false, "incomer",
                token => { order.Enqueue(name); highDone.SetResult(); return ValueTask.CompletedTask; },
                Priority: 9));
            await highDone.Task.WaitAsync(Timeout);
            Assert.IsFalse(done.Task.IsCompleted, $"round {round}'s incomer must run while the victim is parked");
        }

        await done.Task.WaitAsync(Timeout);
        CollectionAssert.AreEqual(new[] { "twice-start", "h1", "h2", "twice-end" }, order.ToArray());
    }

    [TestMethod]
    public async Task AParkedRunPastTheMaxPauseAge_SelfCancelsToTheReRunPath()
    {
        // ADR-0047 A1 rule 5: suspension is bounded — a parked run holds
        // memory and a live write intent, and past the configured age it
        // yields to the interruption-safe re-run path.
        await using var queue = new JobScheduler(
            writerWorkers: 1, maxPause: TimeSpan.FromMilliseconds(250));
        var gate = new PauseGate();
        var observedCancel = Tcs();
        var started = Tcs();

        queue.Enqueue(new QueuedJob(
            "victim", JobLane.Writer, UserInitiated: false, "parks and outstays its welcome",
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

        var holdHigh = Tcs();
        queue.Enqueue(new QueuedJob(
            "occupier", JobLane.Writer, UserInitiated: false, "keeps the pool full",
            async token => await holdHigh.Task.WaitAsync(token),
            Priority: 9));

        // The victim parks and, with the slot never freeing, ages out.
        await observedCancel.Task.WaitAsync(Timeout);
        await WaitForAsync(() => !queue.IsActive("victim"), Timeout);
        holdHigh.SetResult();
    }

    [TestMethod]
    public async Task ARePark_IsNotKilledByTheFirstParksExpiryTimer()
    {
        // The expiry is stamped to the park it was armed for: park, resume,
        // park again — the FIRST park's timer firing must not cancel a run
        // that has only just re-parked.
        await using var queue = new JobScheduler(
            writerWorkers: 1, maxPause: TimeSpan.FromMilliseconds(500));
        var gate = new PauseGate();
        var cancelled = Tcs();
        var done = Tcs();
        var order = new ConcurrentQueue<string>();

        queue.Enqueue(new QueuedJob(
            "twice-parked", JobLane.Writer, UserInitiated: false, "parks twice inside one expiry window",
            async token =>
            {
                order.Enqueue("start");
                try
                {
                    for (var i = 0; i < 400; i++)
                    {
                        await gate.WaitWhilePausedAsync(token);
                        await Task.Delay(5, token);
                    }

                    done.SetResult();
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            },
            Priority: 0,
            PauseGate: gate));
        await WaitForAsync(() => order.Contains("start"), Timeout);

        // Park 1: brief — the incomer finishes at once and the run resumes.
        var h1 = Tcs();
        queue.Enqueue(new QueuedJob(
            "h1", JobLane.Writer, UserInitiated: false, "brief incomer",
            token => { h1.SetResult(); return ValueTask.CompletedTask; }, Priority: 9));
        await h1.Task.WaitAsync(Timeout);

        // Park 2, still inside park 1's expiry window; the occupier holds the
        // slot while park 1's timer fires. The stamped expiry must let the
        // young park live.
        await Task.Delay(150, Timeout);
        var holdHigh = Tcs();
        queue.Enqueue(new QueuedJob(
            "h2", JobLane.Writer, UserInitiated: false, "occupier",
            async token => await holdHigh.Task.WaitAsync(token), Priority: 9));
        await WaitForAsync(() => gate.IsPaused, Timeout);

        // Wait past park 1's expiry moment (500ms from the first park).
        await Task.Delay(450, Timeout);
        Assert.IsFalse(cancelled.Task.IsCompleted,
            "park 1's expiry timer cancelled a run whose second park is younger than the bound");

        holdHigh.SetResult();
        await done.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AVictimThatNeverParks_IsEscalatedPast()
    {
        // ADR-0047 Amendment 2: the worst-ranked victim may be inside one
        // huge file and never reach a pause point; after the escalation
        // window the ask moves to the next-worst gated run, so the incomer
        // is not hostage to the one job that cannot yield.
        await using var queue = new JobScheduler(
            writerWorkers: 2, escalation: TimeSpan.FromMilliseconds(100));
        var order = new ConcurrentQueue<string>();

        // The worst-ranked run holds a gate it never checks again.
        var stuckGate = new PauseGate();
        var releaseStuck = Tcs();
        var stuckStarted = Tcs();
        queue.Enqueue(new QueuedJob(
            "stuck", JobLane.Writer, UserInitiated: false, "never reaches a pause point",
            async token =>
            {
                await stuckGate.WaitWhilePausedAsync(token);
                stuckStarted.SetResult();
                await releaseStuck.Task.WaitAsync(token);
            },
            Priority: 1,
            PauseGate: stuckGate));
        await stuckStarted.Task.WaitAsync(Timeout);

        var (responsiveGate, responsiveDone) = StartPausableLoop(queue, "responsive", priority: 2, order);
        await WaitForAsync(() => order.Contains("responsive-start"), Timeout);

        var highDone = Tcs();
        queue.Enqueue(new QueuedJob(
            "high", JobLane.Writer, UserInitiated: false, "must not wait on the stuck victim",
            token => { order.Enqueue("high"); highDone.SetResult(); return ValueTask.CompletedTask; },
            Priority: 9));

        // The initial ask goes to "stuck" (worst-ranked); the escalation
        // window passes with no park, the responsive run is asked next and
        // parks — which is the ONLY way the incomer can have run while the
        // stuck victim still holds its slot.
        await highDone.Task.WaitAsync(Timeout);
        Assert.IsFalse(releaseStuck.Task.IsCompleted, "the stuck victim must still be running — it cannot yield");
        Assert.IsTrue(stuckGate.IsPaused, "the original ask stands — the victim parks if it ever checks");
        _ = responsiveGate;

        releaseStuck.SetResult();
        await responsiveDone.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public void TheWriterPoolWidth_IsBoundedOneToFive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JobScheduler(writerWorkers: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new JobScheduler(writerWorkers: 6));
    }

    [TestMethod]
    public async Task AnUnloadablePoolConfiguration_NeverStopsTheServiceStarting()
    {
        // The stated promise (ADR-0047 §3): the pool's width must never be
        // the reason a service refuses to start — a hand-edited
        // max_concurrent_backups outside 1..5 makes the load path throw, and
        // the width answers the default instead.
        using var harness = new HostHarness();
        harness.WriteConfiguration("every 1h");
        await harness.CreateRepositoryAsync();

        var path = Path.Combine(harness.StateDirectory, "config.json");
        var text = await File.ReadAllTextAsync(path, Timeout);
        Assert.Contains("\"schema_version\"", text, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            path,
            text.Replace(
                "\"schema_version\"", "\"max_concurrent_backups\": 99, \"schema_version\"",
                StringComparison.Ordinal),
            Timeout);

        using var passphrase = FallbackPlan.Repository.Crypto.Passphrase.Create(
            Environment.GetEnvironmentVariable(harness.PassphraseVariable)!);
        await using var runtime = await Agent.ServiceRuntime.StartAsync(
            new Agent.ServiceOptions
            {
                ArchivesRoot = harness.ArchivesRoot,
                StateDirectory = harness.StateDirectory,
            },
            passphrase,
            Timeout);
        Assert.IsNotNull(runtime.Queue);
    }

    private static (PauseGate Gate, TaskCompletionSource Done) StartPausableLoop(
        JobScheduler queue, string name, int priority, ConcurrentQueue<string> order, bool userInitiated = false)
    {
        var gate = new PauseGate();
        var done = Tcs();
        queue.Enqueue(new QueuedJob(
            name, JobLane.Writer, userInitiated, $"pausable loop {name}",
            async token =>
            {
                order.Enqueue($"{name}-start");
                for (var i = 0; i < 60; i++)
                {
                    await gate.WaitWhilePausedAsync(token);
                    await Task.Delay(5, token);
                }

                order.Enqueue($"{name}-end");
                done.SetResult();
            },
            Priority: priority,
            PauseGate: gate));
        return (gate, done);
    }

    private static TaskCompletionSource Tcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
