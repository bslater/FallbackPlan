using System.Collections.Concurrent;
using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The backup pool and its ordering (ADR-0047): the writer lane widens to a
/// configured number of workers, a set's priority orders waiting work, and a
/// person still outranks any priority. Establishes FR-SVC-012 and
/// FR-SVC-013.
/// </summary>
[TestClass]
public sealed class JobSchedulerPoolTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose() => _timeout.Dispose();

    [TestMethod]
    public async Task TwoWriterWorkers_RunTwoWriterJobsAtOnce()
    {
        await using var queue = new JobScheduler(writerWorkers: 2);

        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        for (var i = 0; i < 2; i++)
        {
            queue.Enqueue(new QueuedJob(
                $"writer-{i}", JobLane.Writer, UserInitiated: false, "hold the lane",
                async token =>
                {
                    if (Interlocked.Increment(ref started) == 2)
                    {
                        bothStarted.TrySetResult();
                    }

                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }));
        }

        await bothStarted.Task.WaitAsync(Timeout);
        release.SetResult();
    }

    [TestMethod]
    public async Task OneWriterWorker_StillSerialises()
    {
        // The pre-pool behaviour, pinned: at one worker, a second writer job
        // cannot start while the first holds the lane.
        await using var queue = new JobScheduler(writerWorkers: 1);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(new QueuedJob(
            "first", JobLane.Writer, UserInitiated: false, "hold",
            async token => { firstStarted.SetResult(); await release.Task.WaitAsync(token).ConfigureAwait(false); }));
        queue.Enqueue(new QueuedJob(
            "second", JobLane.Writer, UserInitiated: false, "wait",
            token => { secondStarted.SetResult(); return ValueTask.CompletedTask; }));

        await firstStarted.Task.WaitAsync(Timeout);
        await Task.Delay(200, Timeout);
        Assert.IsFalse(secondStarted.Task.IsCompleted, "one worker means one running writer job");

        release.SetResult();
        await secondStarted.Task.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task AHigherPriorityJob_DequeuesBeforeALowerOne()
    {
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();
        var (blocked, release) = Occupy(queue);
        await blocked.WaitAsync(Timeout);

        var low = Completion();
        var high = Completion();
        queue.Enqueue(Job("low", order, low, priority: 0));
        queue.Enqueue(Job("high", order, high, priority: 5));

        release();
        await Task.WhenAll(low.Task, high.Task).WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { "high", "low" }, order.ToArray());
    }

    [TestMethod]
    public async Task AUserInitiatedJob_OutranksSetPriority()
    {
        // ADR-0029 §4's rule survives the priority field: a person waiting
        // beats any background priority, however large.
        await using var queue = new JobScheduler(writerWorkers: 1);
        var order = new ConcurrentQueue<string>();
        var (blocked, release) = Occupy(queue);
        await blocked.WaitAsync(Timeout);

        var background = Completion();
        var person = Completion();
        queue.Enqueue(Job("background", order, background, priority: 9));
        queue.Enqueue(new QueuedJob(
            "person", JobLane.Writer, UserInitiated: true, "a person waits",
            token => { order.Enqueue("person"); person.SetResult(); return ValueTask.CompletedTask; },
            Priority: 0));

        release();
        await Task.WhenAll(background.Task, person.Task).WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { "person", "background" }, order.ToArray());
    }

    private static TaskCompletionSource Completion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueuedJob Job(string name, ConcurrentQueue<string> order, TaskCompletionSource done, int priority) =>
        new(
            name, JobLane.Writer, UserInitiated: false, name,
            token => { order.Enqueue(name); done.SetResult(); return ValueTask.CompletedTask; },
            Priority: priority);

    private static (Task Blocked, Action Release) Occupy(JobScheduler queue)
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Enqueue(new QueuedJob(
            "occupy", JobLane.Writer, UserInitiated: true, "occupy",
            async token =>
            {
                blocked.SetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }));

        return (blocked.Task, () => release.TrySetResult());
    }
}
