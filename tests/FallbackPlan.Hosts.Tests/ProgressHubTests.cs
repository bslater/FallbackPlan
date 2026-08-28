using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// When a watcher becomes a subscriber (ADR-0029 §5; FR-SVC-006).
/// </summary>
/// <remarks>
/// A C# <c>async IAsyncEnumerable</c> iterator runs none of its body until the
/// first <c>MoveNextAsync</c>, so a subscription registered inside one does not
/// exist while the caller merely holds the enumerable. Events reported in that
/// window go to nobody as a stream — a subscriber joining later gets exactly
/// one thing per live job, its latest snapshot, never the missed sequence.
/// These tests pin the boundary directly rather than through a job, so they
/// say what the rule is and cannot go quiet under load the way a
/// timing-dependent test can.
/// </remarks>
[TestClass]
public sealed class ProgressHubTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private static JobProgress Progress(string jobId, JobState state) =>
        new(jobId, state, FilesSeen: 10, FilesDone: 0, FilesReused: 0, FilesFailed: 0, BytesSeen: 4096, BytesStored: 0);

    [TestMethod]
    public async Task Watch_WhenCalled_ShouldSubscribeImmediatelyRatherThanOnFirstEnumeration()
    {
        var hub = new ProgressHub();

        // Deciding to watch is what subscribes. Reporting happens here before
        // anyone pulls, which is exactly the window a job's first state lands
        // in when the watcher was handed to a thread pool that has not run it
        // yet. If this event is lost the enumeration below never completes and
        // the timeout fails the test.
        var events = hub.WatchAsync(_timeout.Token);
        hub.Report(Progress("job-1", JobState.Scanning));

        await using var enumerator = events.GetAsyncEnumerator(_timeout.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual("job-1", enumerator.Current.Progress.JobId);
        Assert.AreEqual(JobState.Scanning, enumerator.Current.Progress.State);
    }

    [TestMethod]
    public async Task Watch_EventsReportedBeforeTheCall_ReplaysOnlyTheLatestSnapshotOfTheJob()
    {
        var hub = new ProgressHub();

        // The other half of the boundary: there is no historical backlog. A
        // console arriving mid-run needs the job's CURRENT numbers to render
        // a meter at once — but never the sequence it missed; durable answers
        // still come from status, which is derived from durable state
        // (10 §3.1). Two reports before the call, one snapshot delivered.
        hub.Report(Progress("job-1", JobState.Scanning));
        hub.Report(Progress("job-1", JobState.Packing));

        var events = hub.WatchAsync(_timeout.Token);
        hub.Complete();

        var delivered = new List<JobProgressEvent>();
        await foreach (var progressEvent in events)
        {
            delivered.Add(progressEvent);
        }

        var replayed = Assert.ContainsSingle(delivered);
        Assert.AreEqual(JobState.Packing, replayed.Progress.State);
    }

    [TestMethod]
    public async Task Watch_ASubscriberArrivingMidRun_ReceivesTheLatestSnapshotPerLiveJob()
    {
        var hub = new ProgressHub();

        hub.Report(Progress("job-1", JobState.Scanning));
        hub.Report(Progress("job-1", JobState.Uploading));
        hub.Report(Progress("job-2", JobState.Scanning));

        var events = hub.WatchAsync(_timeout.Token);
        hub.Complete();

        var replayed = new Dictionary<string, JobState>();
        await foreach (var progressEvent in events)
        {
            replayed[progressEvent.Progress.JobId] = progressEvent.Progress.State;
        }

        Assert.HasCount(2, replayed);
        Assert.AreEqual(JobState.Uploading, replayed["job-1"]);
        Assert.AreEqual(JobState.Scanning, replayed["job-2"]);
    }

    [TestMethod]
    public async Task Watch_AJobThatSettled_IsNotReplayed()
    {
        var hub = new ProgressHub();

        // A settled job's story belongs to the journal, not the live feed: a
        // console connecting after the run would otherwise render a finished
        // job as live until the next poll corrected it.
        hub.Report(Progress("job-1", JobState.Packing));
        hub.Report(Progress("job-1", JobState.Complete));
        hub.Report(Progress("job-2", JobState.Packing));

        var events = hub.WatchAsync(_timeout.Token);
        hub.Complete();

        var delivered = new List<JobProgressEvent>();
        await foreach (var progressEvent in events)
        {
            delivered.Add(progressEvent);
        }

        var replayed = Assert.ContainsSingle(delivered);
        Assert.AreEqual("job-2", replayed.Progress.JobId);
    }

    [TestMethod]
    public async Task Complete_AWatcherThatNeverEnumerated_EndsItCleanly()
    {
        var hub = new ProgressHub();

        // Subscribing at the call means an abandoned watcher is a real
        // subscriber, so a stopping service has to be able to end one that
        // never pulled. Its queue is bounded and drop-oldest, so the cost of
        // abandoning one is capped rather than unbounded — but it must still
        // be closable, or a service could not shut down.
        var events = hub.WatchAsync(_timeout.Token);
        hub.Complete();

        await using var enumerator = events.GetAsyncEnumerator(_timeout.Token);

        Assert.IsFalse(await enumerator.MoveNextAsync());
    }

    [TestMethod]
    public async Task ProgressHub_SeveralWatchers_DeliversTheSameEventToEach()
    {
        var hub = new ProgressHub();

        var first = hub.WatchAsync(_timeout.Token);
        var second = hub.WatchAsync(_timeout.Token);
        hub.Report(Progress("job-1", JobState.Publishing));

        await using var firstEvents = first.GetAsyncEnumerator(_timeout.Token);
        await using var secondEvents = second.GetAsyncEnumerator(_timeout.Token);

        Assert.IsTrue(await firstEvents.MoveNextAsync());
        Assert.IsTrue(await secondEvents.MoveNextAsync());

        // One report, one sequence number, however many watchers.
        Assert.AreEqual(firstEvents.Current.Sequence, secondEvents.Current.Sequence);
        Assert.AreEqual(JobState.Publishing, firstEvents.Current.Progress.State);
    }

    /// <inheritdoc />
    public void Dispose() => _timeout.Dispose();
}
