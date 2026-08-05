using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Agent;

/// <summary>
/// Fans job progress out to whatever clients are watching (ADR-0029 §5).
/// </summary>
/// <remarks>
/// <para>
/// Every subscriber gets a <b>bounded</b> queue that drops its oldest entry
/// when full. A watcher that stops reading must not be able to stall the
/// engine: progress is a courtesy to a UI, and a backup that slowed down
/// because a window was minimised would be a worse product than one that
/// skipped a frame.
/// </para>
/// <para>
/// Progress does not survive a restart, deliberately (10 §3.1). A job that
/// restarted is a new stream; durable answers come from status, which is
/// derived from durable state.
/// </para>
/// </remarks>
public sealed class ProgressHub : IJobProgressReporter
{
    private readonly List<Channel<JobProgressEvent>> _subscribers = [];
    private readonly Lock _gate = new();
    private long _sequence;

    /// <summary>The most recent observation per job, for a client that arrives late.</summary>
    public Dictionary<string, JobProgress> Latest { get; } = [];

    /// <inheritdoc/>
    public void Report(JobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var published = new JobProgressEvent(Interlocked.Increment(ref _sequence), progress);

        lock (_gate)
        {
            Latest[progress.JobId] = progress;
            foreach (var subscriber in _subscribers)
            {
                // Bounded and drop-oldest: TryWrite never blocks the engine.
                subscriber.Writer.TryWrite(published);
            }
        }
    }

    /// <summary>Streams progress to one watcher until it stops listening.</summary>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>The events.</returns>
    public async IAsyncEnumerable<JobProgressEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<JobProgressEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return progress;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <summary>Ends every subscription, so a stopping service does not leave watchers hanging.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}
