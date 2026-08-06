using FallbackPlan.Agent;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// When a watcher becomes a subscriber (ADR-0029 §5; FR-SVC-006).
/// </summary>
/// <remarks>
/// A C# <c>async IAsyncEnumerable</c> iterator runs none of its body until the
/// first <c>MoveNextAsync</c>, so a subscription registered inside one does not
/// exist while the caller merely holds the enumerable. Everything reported in
/// that window goes to nobody, and nothing replays it. These tests pin the
/// boundary directly rather than through a job, so they say what the rule is
/// and cannot go quiet under load the way a timing-dependent test can.
/// </remarks>
public sealed class ProgressHubTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private static JobProgress Progress(string jobId, JobState state) =>
        new(jobId, state, FilesSeen: 10, FilesDone: 0, FilesReused: 0, FilesFailed: 0, BytesSeen: 4096, BytesStored: 0);

    [Fact]
    public async Task A_watcher_is_subscribed_from_the_call_not_from_the_first_enumeration()
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

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("job-1", enumerator.Current.Progress.JobId);
        Assert.Equal(JobState.Scanning, enumerator.Current.Progress.State);
    }

    [Fact]
    public async Task A_watcher_receives_nothing_reported_before_it_asked_to_watch()
    {
        var hub = new ProgressHub();

        // The other half of the boundary, and deliberate: there is no replay.
        // Progress is a live courtesy to a UI, and durable answers come from
        // status, which is derived from durable state (10 §3.1).
        hub.Report(Progress("job-1", JobState.Scanning));

        var events = hub.WatchAsync(_timeout.Token);
        hub.Report(Progress("job-1", JobState.Packing));

        await using var enumerator = events.GetAsyncEnumerator(_timeout.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(JobState.Packing, enumerator.Current.Progress.State);
    }

    [Fact]
    public async Task Completing_ends_a_watcher_that_never_enumerated()
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

        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Every_watcher_sees_the_same_event()
    {
        var hub = new ProgressHub();

        var first = hub.WatchAsync(_timeout.Token);
        var second = hub.WatchAsync(_timeout.Token);
        hub.Report(Progress("job-1", JobState.Publishing));

        await using var firstEvents = first.GetAsyncEnumerator(_timeout.Token);
        await using var secondEvents = second.GetAsyncEnumerator(_timeout.Token);

        Assert.True(await firstEvents.MoveNextAsync());
        Assert.True(await secondEvents.MoveNextAsync());

        // One report, one sequence number, however many watchers.
        Assert.Equal(firstEvents.Current.Sequence, secondEvents.Current.Sequence);
        Assert.Equal(JobState.Publishing, firstEvents.Current.Progress.State);
    }

    /// <inheritdoc />
    public void Dispose() => _timeout.Dispose();
}
