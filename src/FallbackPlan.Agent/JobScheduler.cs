using Bodu;
using FallbackPlan.Domain.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <param name="Priority">
/// The configured priority carried from the set (a backup) or the destination
/// (a sync) — higher wins among waiting work of the same initiation
/// (ADR-0047). It never outranks a person: <paramref name="UserInitiated"/>
/// sorts first, exactly as ADR-0029 §4 always said.
/// </param>
/// <param name="PauseGate">
/// The job's suspension point, when it has one (ADR-0047 Amendment 1). A writer-lane
/// job carrying a gate is preemptible: when every writer worker is busy and
/// a higher-ranked job arrives, the scheduler pauses the lowest-ranked
/// gated job, runs the incomer in the freed slot, and resumes the parked
/// run when a slot frees again. A job without a gate is never paused — it
/// merely cannot yield, so the incomer waits behind it.
/// </param>
public sealed record QueuedJob(
    string JobId,
    JobLane Lane,
    bool UserInitiated,
    string Description,
    Func<CancellationToken, ValueTask> Run,
    int Priority = 0,
    PauseGate? PauseGate = null);

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
    private readonly PriorityQueue<QueuedJob, (int Initiation, int Priority, long Arrival)> _writerLane = new();
    private readonly PriorityQueue<QueuedJob, (int Initiation, int Priority, long Arrival)> _readerLane = new();
    private readonly PriorityQueue<QueuedJob, (int Initiation, int Priority, long Arrival)> _transferLane = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];

    // The writer lane's preemption bookkeeping (ADR-0047 Amendment 1). A writer job a
    // worker is actually running sits in _attended; one that parked at its
    // pause gate — its task alive, its worker handed to someone else — sits
    // in _paused. Both are keyed by job identity; both are guarded by _gate.
    private readonly Dictionary<string, WriterAttendance> _attended = [];
    private readonly Dictionary<string, WriterAttendance> _paused = [];
    private readonly int _writerWorkers;
    private readonly TimeSpan _maxPause;

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
    private readonly CancellationToken _stoppingToken;
    private readonly Task[] _workers;
    private readonly ILogger _log;
    private long _arrival;

    /// <summary>Starts the queue's workers.</summary>
    /// <param name="log">Where to report a job that failed outside its own handler.</param>
    /// <param name="writerWorkers">
    /// The backup pool's width (ADR-0047): how many writer-lane jobs may run
    /// at once, 1..5. Safe by construction at more than one because each
    /// set's staging archive, writer sequence, spool and catalogue are its
    /// own; the cap exists because past a handful they contend for the same
    /// disk and mostly make each other slower.
    /// </param>
    /// <param name="maxPause">
    /// How long a parked run may hold its in-memory state and its live write
    /// intent before it self-cancels to the interruption-safe re-run path —
    /// the guard against a busy pool pinning a suspended capture's memory
    /// for ever. An hour by default.
    /// </param>
    public JobScheduler(ILogger? log = null, int writerWorkers = 1, TimeSpan? maxPause = null)
    {
        ThrowHelper.ThrowIfOutOfRange(writerWorkers, 1, 5);
        _log = log ?? NullLogger.Instance;
        _writerWorkers = writerWorkers;
        _maxPause = maxPause ?? TimeSpan.FromHours(1);
        _stoppingToken = _stopping.Token;

        // The reader lane stays one worker because restores are themselves
        // internally bounded and a second would only compete for the same
        // disk; the transfer lane stays one because destinations mostly
        // contend for the same uplink — widening it per destination is the
        // anticipated axis, taken on measurement, not speculatively.
        _workers =
        [
            .. Enumerable.Range(0, writerWorkers).Select(_ => Task.Run(() => PumpAsync(JobLane.Writer))),
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
            // work already waiting, then the configured priority (negated, so
            // a higher number wins), and ties break by arrival so nothing
            // starves.
            var initiation = job.UserInitiated ? 0 : 1;
            var lane = job.Lane switch
            {
                JobLane.Writer => _writerLane,
                JobLane.Reader => _readerLane,
                _ => _transferLane,
            };
            var key = (initiation, -job.Priority, Interlocked.Increment(ref _arrival));
            lane.Enqueue(job, key);
            _running[job.JobId] = new CancellationTokenSource();

            if (job.Lane == JobLane.Writer)
            {
                MaybePreemptLocked(key);
            }
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

        // A parked run has no worker attending it, so awaiting the workers
        // alone would leave its task mid-cancellation. Its own token was
        // cancelled above, which cuts through the park; this waits for the
        // exit to actually land.
        Task[] parked;
        lock (_gate)
        {
            parked = [.. _paused.Values.Select(entry => entry.RunTask).OfType<Task>()];
        }

        try
        {
            await Task.WhenAll([.. _workers, .. parked]).ConfigureAwait(false);
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

            if (lane == JobLane.Writer)
            {
                if (TryTakeWriterWork(out var work))
                {
                    await AttendWriterAsync(work!).ConfigureAwait(false);
                }

                continue;
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
                Log.JobFaulted(_log, job!.JobId, job.Description, exception);
            }
            finally
            {
                Complete(job!.JobId);
            }
        }
    }

    /// <summary>
    /// Picks the writer worker's next charge: the best-ranked parked run when
    /// it outranks (or ties) everything still queued — a freed slot resumes
    /// suspended work before starting new work of equal standing — and the
    /// queue's head otherwise.
    /// </summary>
    private bool TryTakeWriterWork(out WriterAttendance? work)
    {
        lock (_gate)
        {
            WriterAttendance? bestPaused = null;
            foreach (var entry in _paused.Values)
            {
                if (bestPaused is null || entry.Key.CompareTo(bestPaused.Key) < 0)
                {
                    bestPaused = entry;
                }
            }

            var hasQueued = _writerLane.TryPeek(out _, out var queuedKey);
            if (bestPaused is not null && (!hasQueued || bestPaused.Key.CompareTo(queuedKey) <= 0))
            {
                _paused.Remove(bestPaused.Job.JobId);
                _attended[bestPaused.Job.JobId] = bestPaused;
                bestPaused.NeedsResume = true;
                work = bestPaused;
                return true;
            }

            if (_writerLane.TryDequeue(out var job, out var key) && _running.TryGetValue(job.JobId, out var source))
            {
                var fresh = new WriterAttendance(job, key, source);
                _attended[job.JobId] = fresh;
                work = fresh;
                return true;
            }

            work = null;
            return false;
        }
    }

    /// <summary>
    /// Attends one writer charge until it finishes or parks. A parked run
    /// keeps its task and its state; the worker hands its slot on — that is
    /// the whole preemption mechanism (ADR-0047 Amendment 1).
    /// </summary>
    private async Task AttendWriterAsync(WriterAttendance work)
    {
        var gate = work.Job.PauseGate;
        if (work.NeedsResume)
        {
            work.NeedsResume = false;
            gate!.Resume();
        }

        // The supervision task starts here, outside the pickup lock, because
        // an async method runs synchronously to its first await — which is
        // the job's own code.
        work.RunTask ??= SuperviseAsync(work.Job, work.Cancellation);

        if (gate is null)
        {
            await work.RunTask.ConfigureAwait(false);
            lock (_gate)
            {
                _attended.Remove(work.Job.JobId);
            }

            return;
        }

        var finished = await Task.WhenAny(work.RunTask, gate.Parked).ConfigureAwait(false);
        if (finished == work.RunTask || work.RunTask.IsCompleted)
        {
            await work.RunTask.ConfigureAwait(false);
            lock (_gate)
            {
                _attended.Remove(work.Job.JobId);
            }

            return;
        }

        // The job parked. Move it aside, arm the max-pause guard, and free
        // this worker: the released token is the parked run's claim on the
        // next slot, weighed against the queue at every pickup.
        lock (_gate)
        {
            _attended.Remove(work.Job.JobId);
            _paused[work.Job.JobId] = work;
        }

        _ = ExpireIfStillPausedAsync(work.Job.JobId);
        _writerPending.Release();
    }

    /// <summary>
    /// Runs one writer job to its end, wherever the awaiting worker has got
    /// to — completion bookkeeping lives here precisely so a job cancelled
    /// while parked still closes out, with no worker attending it.
    /// </summary>
    private async Task SuperviseAsync(QueuedJob job, CancellationTokenSource cancellation)
    {
        try
        {
            await job.Run(cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.JobFaulted(_log, job.JobId, job.Description, exception);
        }
        finally
        {
            lock (_gate)
            {
                _paused.Remove(job.JobId);
            }

            Complete(job.JobId);
        }
    }

    /// <summary>
    /// Pauses the worst-ranked attended writer job when the incomer outranks
    /// it and no worker is free. Called under <see cref="_gate"/> from
    /// <see cref="Enqueue"/>; a gate whose job never reaches another pause
    /// point simply finishes instead — the request costs nothing.
    /// </summary>
    private void MaybePreemptLocked((int Initiation, int Priority, long Arrival) incomer)
    {
        if (_attended.Count < _writerWorkers)
        {
            return;
        }

        WriterAttendance? victim = null;
        foreach (var entry in _attended.Values)
        {
            if (entry.Job.PauseGate is { IsPaused: false }
                && (victim is null || entry.Key.CompareTo(victim.Key) > 0))
            {
                victim = entry;
            }
        }

        if (victim is not null && incomer.CompareTo(victim.Key) < 0)
        {
            victim.Job.PauseGate!.Pause();
        }
    }

    private async Task ExpireIfStillPausedAsync(string jobId)
    {
        try
        {
            await Task.Delay(_maxPause, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool stillPaused;
        lock (_gate)
        {
            stillPaused = _paused.ContainsKey(jobId);
        }

        if (stillPaused)
        {
            // Holding a run suspended costs its in-memory state and a live
            // write intent; past the configured age that price stops being
            // worth paying, and the interruption-safe re-run path takes over.
            Cancel(jobId);
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

    /// <summary>
    /// One writer job in a worker's charge: its queue key (for preemption
    /// and resumption ranking), its cancellation, and — once started — the
    /// supervision task that outlives any one worker's attention.
    /// </summary>
    private sealed class WriterAttendance(
        QueuedJob job, (int Initiation, int Priority, long Arrival) key, CancellationTokenSource cancellation)
    {
        public QueuedJob Job { get; } = job;

        public (int Initiation, int Priority, long Arrival) Key { get; } = key;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? RunTask { get; set; }

        public bool NeedsResume { get; set; }
    }
}
