using Bodu;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Agent;

/// <summary>Which lane a job runs in — decided by whether it takes the writer role.</summary>
public enum JobLane
{
    /// <summary>Backups. They take the writer sequence, so they run one at a time.</summary>
    Writer = 0,

    /// <summary>Restores and verification. Read paths, so they may run alongside a backup.</summary>
    Reader = 1,

    /// <summary>
    /// Fan-out to destinations (ADR-0029 §4 amendment). Copying sealed objects
    /// takes no writer role, so the writer lane is wrong for it; and it is
    /// long-running background transfer a user's restore must never wait
    /// behind, so the reader lane is wrong too. One worker: destinations
    /// mostly contend for the same uplink.
    /// </summary>
    Transfer = 2,
}

/// <summary>One queued piece of work.</summary>
/// <param name="JobId">The job's identity, for progress and cancellation.</param>
/// <param name="Lane">Which lane it runs in.</param>
/// <param name="UserInitiated">Whether a person is waiting for it.</param>
/// <param name="Description">What to call it in a status line.</param>
/// <param name="Run">The work.</param>
public sealed record QueuedJob(
    string JobId,
    JobLane Lane,
    bool UserInitiated,
    string Description,
    Func<CancellationToken, ValueTask> Run);

/// <summary>
/// Service-level concurrency (ADR-0029 §4). ADR-0028 gave the service the sole
/// writer role, which makes this a scheduling question inside one process
/// rather than a locking question across several.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Backup sets run one at a time.</b> They contend for the same disk and the
/// same writer sequence, and two at once mostly makes both slower while
/// doubling the memory bound.
/// </description></item>
/// <item><description>
/// <b>Restore and verification are separately queued</b> and may run alongside a
/// backup. A user waiting on a restore must not wait for a scheduled backup to
/// finish; a restore is a read path and does not take the writer role.
/// </description></item>
/// <item><description>
/// <b>A user-initiated operation outranks a scheduled one.</b> Where they
/// contend, background work yields — the concrete meaning of NFR-PERF-013's
/// "background activity shall observe configured limits".
/// </description></item>
/// </list>
/// </remarks>
public sealed class JobScheduler : IAsyncDisposable
{
    private readonly PriorityQueue<QueuedJob, (int Priority, long Arrival)> _writerLane = new();
    private readonly PriorityQueue<QueuedJob, (int Priority, long Arrival)> _readerLane = new();
    private readonly PriorityQueue<QueuedJob, (int Priority, long Arrival)> _transferLane = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    // One signal per lane, not one shared: with a shared semaphore, a token
    // released for a busy lane could only be consumed by the OTHER lanes'
    // workers, which handed it back and re-waited at thread-pool speed for
    // the whole duration of the running job — a core burnt for the length
    // of a multi-hour transfer with a second one queued.
    private readonly SemaphoreSlim _writerPending = new(0);
    private readonly SemaphoreSlim _readerPending = new(0);
    private readonly SemaphoreSlim _transferPending = new(0);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task[] _workers;
    private readonly Action<string, Exception?>? _log;
    private long _arrival;

    /// <summary>Starts the queue's workers.</summary>
    /// <param name="log">Where to report a job that failed outside its own handler.</param>
    public JobScheduler(Action<string, Exception?>? log = null)
    {
        _log = log;

        // One worker per lane. The writer lane is one by decision, not by
        // accident; the reader lane is one for now because restores are
        // themselves internally bounded and a second would only compete for
        // the same disk; the transfer lane is one because destinations mostly
        // contend for the same uplink — widening it per destination is the
        // anticipated axis, taken on measurement, not speculatively.
        _workers =
        [
            Task.Run(() => PumpAsync(JobLane.Writer)),
            Task.Run(() => PumpAsync(JobLane.Reader)),
            Task.Run(() => PumpAsync(JobLane.Transfer)),
        ];
    }

    /// <summary>How many jobs are running right now.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>Whether a job with this identity is queued or running.</summary>
    /// <param name="jobId">The job identity.</param>
    /// <returns><see langword="true"/> when it is known.</returns>
    public bool IsActive(string jobId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(jobId);
        }
    }

    /// <summary>
    /// Queues a job. A job whose identity is already queued or running is
    /// refused outright — the coalescing rule (ADR-0029 §4 amendment): the
    /// duplicate is dropped, not queued behind, and the catch-up comes from
    /// the next scheduler pass re-evaluating the pair, so a slow destination
    /// faces one retry stream rather than a backlog.
    /// </summary>
    /// <param name="job">The work.</param>
    /// <returns><see langword="true"/> when queued; <see langword="false"/> when the identity is already active.</returns>
    public bool Enqueue(QueuedJob job)
    {
        ThrowHelper.ThrowIfNull(job);

        lock (_gate)
        {
            if (_running.ContainsKey(job.JobId))
            {
                return false;
            }

            // Lower sorts first: a user-initiated job jumps ahead of scheduled
            // work already waiting, and ties break by arrival so nothing starves.
            var priority = job.UserInitiated ? 0 : 1;
            var lane = job.Lane switch
            {
                JobLane.Writer => _writerLane,
                JobLane.Reader => _readerLane,
                _ => _transferLane,
            };
            lane.Enqueue(job, (priority, Interlocked.Increment(ref _arrival)));
            _running[job.JobId] = new CancellationTokenSource();
        }

        Pending(job.Lane).Release();
        return true;
    }

    /// <summary>
    /// Cancels a queued or running job. Cancellation is a command, not a signal
    /// (ADR-0029 §4) — the runner records <see cref="JobState.Cancelled"/>.
    /// </summary>
    /// <param name="jobId">The job to stop.</param>
    /// <returns><see langword="true"/> when a job by that identity was found.</returns>
    public bool Cancel(string jobId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(jobId, out var cancellation))
            {
                return false;
            }

            cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Stops the queue, cancelling everything in flight.</summary>
    /// <returns>A task that completes when the workers have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var cancellation in _running.Values)
            {
                cancellation.Cancel();
            }
        }

        // Wake every worker so each observes the stop.
        _writerPending.Release();
        _readerPending.Release();
        _transferPending.Release();

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not a failure.
        }

        lock (_gate)
        {
            foreach (var cancellation in _running.Values)
            {
                cancellation.Dispose();
            }

            _running.Clear();
        }

        _writerPending.Dispose();
        _readerPending.Dispose();
        _transferPending.Dispose();
        _stopping.Dispose();
    }

    private SemaphoreSlim Pending(JobLane lane) => lane switch
    {
        JobLane.Writer => _writerPending,
        JobLane.Reader => _readerPending,
        _ => _transferPending,
    };

    private async Task PumpAsync(JobLane lane)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Pending(lane).WaitAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!TryDequeue(lane, out var job, out var cancellation))
            {
                // A token with no job behind it — the queue was drained by
                // disposal. Nothing to hand anywhere: each lane's tokens are
                // its own.
                continue;
            }

            try
            {
                await job!.Run(cancellation!.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A job that throws past its own handler must not take the
                // service down with it; the next scheduled pass still runs.
                _log?.Invoke($"job {job!.JobId} ({job.Description}) failed", exception);
            }
            finally
            {
                Complete(job!.JobId);
            }
        }
    }

    private bool TryDequeue(JobLane lane, out QueuedJob? job, out CancellationTokenSource? cancellation)
    {
        lock (_gate)
        {
            var queue = lane switch
            {
                JobLane.Writer => _writerLane,
                JobLane.Reader => _readerLane,
                _ => _transferLane,
            };
            if (queue.TryDequeue(out var dequeued, out _) && _running.TryGetValue(dequeued.JobId, out var source))
            {
                job = dequeued;
                cancellation = source;
                return true;
            }

            job = null;
            cancellation = null;
            return false;
        }
    }

    private void Complete(string jobId)
    {
        lock (_gate)
        {
            if (_running.Remove(jobId, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }
}
