using Bodu;
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
    private readonly Dictionary<string, JobProgressEvent> _latest = [];
    private readonly Lock _gate = new();
    private long _sequence;

    /// <inheritdoc/>
    public void Report(JobProgress progress)
    {
        ThrowHelper.ThrowIfNull(progress);

        lock (_gate)
        {
            // Numbered and delivered under one lock. Allocating the sequence
            // outside it would let two reports enter in the opposite order to
            // their numbers, so a watcher could see 5 before 4 — and the
            // sequence exists precisely so a client can spot a gap.
            var published = new JobProgressEvent(++_sequence, progress);

            // The per-job snapshot a later subscriber is handed on arrival.
            // A settled job leaves the map: its story belongs to the journal,
            // and replaying it would render a finished job as live.
            if (HasSettled(progress.State))
            {
                _latest.Remove(progress.JobId);
            }
            else
            {
                _latest[progress.JobId] = published;
            }

            foreach (var subscriber in _subscribers)
            {
                // Bounded and drop-oldest: TryWrite never blocks the engine.
                subscriber.Writer.TryWrite(published);
            }
        }
    }

    private static bool HasSettled(JobState state) => state is
        JobState.Complete
        or JobState.CompletedWithFailures
        or JobState.Cancelled
        or JobState.FailedRecoverable
        or JobState.FailedPermanent;

    /// <summary>Streams progress to one watcher until it stops listening.</summary>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>The events.</returns>
    /// <remarks>
    /// <para>
    /// Subscription happens <b>here</b>, in a method that is deliberately not
    /// an iterator, rather than in the streaming body below. An
    /// <c>async IAsyncEnumerable</c> runs none of its body until the first
    /// <c>MoveNextAsync</c>, so registering inside one leaves a caller who
    /// holds the enumerable — and believes it is watching — subscribed to
    /// nothing. <see cref="Report"/> writes only to registered subscribers,
    /// and the only replay is the latest snapshot per live job, so the
    /// missed sequence in that window was lost. For a UI attaching to a
    /// running job that is silent data loss; for the service tests it was a
    /// flake that only appeared when the thread pool was busy.
    /// </para>
    /// <para>
    /// The trade is deliberate: a caller that asks to watch and never
    /// enumerates now holds a real subscription until <see cref="Complete"/>,
    /// where before it held none. That is the right side to err on — the queue
    /// is bounded and drop-oldest, so an abandoned watcher costs at most its
    /// 256 slots, while the alternative costs events nobody can tell were
    /// missing.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<JobProgressEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        lock (_gate)
        {
            // The latest snapshot per live job rides ahead of the live feed —
            // never the missed sequence, just enough for a console arriving
            // mid-run to render a meter at once instead of waiting for the
            // next file to complete (which, mid-enormous-file, can be a long
            // wait). Written before the subscription is visible to Report,
            // so a concurrent report cannot interleave behind its own
            // snapshot.
            foreach (var snapshot in _latest.Values)
            {
                channel.Writer.TryWrite(snapshot);
            }

            _subscribers.Add(channel);
        }

        return StreamAsync(channel, cancellationToken);
    }

    private async IAsyncEnumerable<JobProgressEvent> StreamAsync(
        Channel<JobProgressEvent> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
