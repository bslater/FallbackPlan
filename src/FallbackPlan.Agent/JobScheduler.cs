using System.Diagnostics;
using Bodu;
using FallbackPlan.Domain.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>Which lane a job runs in — decided by whether it takes the writer role.</summary>
public enum JobLane
{
    /// <summary>
    /// Backups. Each takes its own set's writer sequence, so the lane runs a
    /// configured pool of them (ADR-0047) — one per set at a time.
    /// </summary>
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
/// <b>Backups run as a pool of 1..5 workers</b> (ADR-0047): per-set archives
/// gave every set its own writer sequence, spool and catalogue, so two runs
/// contend only for the disk — which is what the cap and the modest default
/// guard. One run per set at a time still holds, enforced by the journal.
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
/// <item><description>
/// <b>Background work can be held out of the pool altogether</b>
/// (<see cref="HoldBackgroundAsync"/>, ADR-0069): the first of NFR-PERF-013's
/// four named limits, a configured time window, is enforced here because this
/// is the only thing that knows which runs are attended and which are parked.
/// The hold borrows the preemption machinery whole and adds one rule — while
/// it stands, the writer pump neither resumes a parked background run nor
/// starts a queued one.
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
    private readonly TimeSpan _escalationDelay;
    private long _parkSequence;

    // The background hold (ADR-0069). Guarded by _gate, like the two
    // dictionaries above, because every reader of it is already holding
    // the gate to read them.
    private bool _backgroundHeld;

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
    private bool _disposed;

    /// <summary>Starts the queue's workers.</summary>
    /// <param name="log">Where to report a job that failed outside its own handler.</param>
    /// <param name="writerWorkers">
    /// The backup pool's width (ADR-0047): how many writer-lane jobs may run
    /// at once, 1..5. Safe by construction at more than one because each
    /// set's archive — staging or direct-ship metadata store (ADR-0046) —
    /// with its writer sequence, spool and catalogue is its own; the cap
    /// exists because past a handful they contend for the same
    /// disk and mostly make each other slower.
    /// </param>
    /// <param name="maxPause">
    /// How long a parked run may hold its in-memory state and its live write
    /// intent before it self-cancels to the interruption-safe re-run path —
    /// the guard against a busy pool pinning a suspended capture's memory
    /// for ever. An hour by default.
    /// </param>
    /// <param name="escalation">
    /// How long a preemption ask may go unanswered before it moves to the
    /// next-worst gated run (ADR-0047 Amendment 2) — the worst-ranked victim
    /// may be inside one huge file and unable to yield while a responsive
    /// run sits beside it. Two seconds by default.
    /// </param>
    public JobScheduler(
        ILogger? log = null, int writerWorkers = 1, TimeSpan? maxPause = null, TimeSpan? escalation = null)
    {
        ThrowHelper.ThrowIfOutOfRange(writerWorkers, 1, 5);
        _log = log ?? NullLogger.Instance;
        _writerWorkers = writerWorkers;
        _maxPause = maxPause ?? TimeSpan.FromHours(1);
        _escalationDelay = escalation ?? TimeSpan.FromSeconds(2);
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
    /// Holds background work out of the writer pool: every attended, gated,
    /// non-user-initiated run is asked to park, and while the hold stands the
    /// pump resumes no parked background run and starts no queued one
    /// (ADR-0069). Idempotent, so a shut window may simply ask on every pass
    /// rather than remembering a transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The standing state is the mechanism and the ask is only its first
    /// half. ADR-0047's pump resumes the best-ranked parked run the moment a
    /// worker frees — which is precisely what a park does — so a one-shot
    /// <see cref="PauseGate.Pause"/> would be undone within milliseconds by
    /// the worker the park released.
    /// </para>
    /// <para>
    /// The wait is for the <em>report</em>, never for correctness: a run
    /// inside one huge file may not reach a boundary inside the settle
    /// window, and it is still asked, still parks later, and is still held
    /// when it does. A hold whose completion depended on a capture reaching a
    /// pause point would be a pass one large file could hang.
    /// </para>
    /// <para>
    /// A parked run is <em>not</em> held indefinitely: the max-pause cap
    /// (see the constructor) is armed at the park and is unchanged by the
    /// hold, so a closure that outlasts it hands the run to the
    /// interruption-safe re-run path — which is exactly right for a window
    /// that stays shut for hours.
    /// </para>
    /// </remarks>
    /// <param name="reason">
    /// Why, in the words the journal and the progress surface will carry —
    /// the pass's, because this knows nothing of windows and a run's park
    /// reason is what a person reads to answer "why did my backup stop".
    /// </param>
    /// <param name="cancellationToken">Cuts the settle wait short; the hold itself is already in force.</param>
    /// <returns>How many background runs are parked under the hold.</returns>
    public async ValueTask<int> HoldBackgroundAsync(string reason, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(reason);

        List<(Task Parked, Task? Run)> asked = [];
        lock (_gate)
        {
            _backgroundHeld = true;
            foreach (var entry in _attended.Values)
            {
                if (entry.Job.UserInitiated || entry.Job.PauseGate is not { IsPaused: false } gate)
                {
                    continue;
                }

                gate.Pause(reason);
                asked.Add((gate.Parked, entry.RunTask));
            }
        }

        // Either signal ends the wait for one run: a job asked to park may
        // instead finish, which is the same outcome from the pool's point of
        // view and is why AttendWriterAsync waits on the pair too. The whole
        // settle is bounded by one escalation delay across every ask, and
        // running out of it is an ordinary outcome rather than a failure —
        // the run stays asked, parks later, and is held when it does.
        var started = Stopwatch.GetTimestamp();
        foreach (var (parked, run) in asked)
        {
            var remaining = _escalationDelay - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.WhenAny(parked, run ?? parked).WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        lock (_gate)
        {
            // Counted from the gates and not from the paused set: the park
            // signal fires on the job's own thread, and the attending worker
            // moves the entry across a moment later, so a count of _paused
            // taken the instant the wait returned would race that hand-over
            // and under-report by one.
            return _attended.Values.Concat(_paused.Values).Count(entry =>
                !entry.Job.UserInitiated && entry.Job.PauseGate is { Parked.IsCompleted: true });
        }
    }

    /// <summary>
    /// Lifts the hold and wakes the pump so held work is reconsidered
    /// (ADR-0069). Idempotent; a release with nothing held costs the workers
    /// one turn each round the loop.
    /// </summary>
    /// <remarks>
    /// The resume itself is the pump's, not this method's: a released run
    /// re-enters the ordinary ranking at the next pickup and takes a slot
    /// when its rank says it may, rather than all of them starting at once
    /// the instant a window opens.
    /// </remarks>
    public void ReleaseBackground()
    {
        int wake;
        lock (_gate)
        {
            if (!_backgroundHeld)
            {
                return;
            }

            _backgroundHeld = false;

            // One token per held run plus one per worker. Over-releasing is
            // the already-handled "a token with no job behind it" case;
            // under-releasing is a pool that sleeps through the opening.
            wake = _paused.Count + _writerWorkers;
        }

        _writerPending.Release(wake);
    }

    /// <summary>
    /// Queues a job. A job whose identity is already queued or running is
    /// refused outright — the coalescing rule (ADR-0029 §4 amendment): the
    /// duplicate is dropped, not queued behind, and the catch-up comes from
    /// the next scheduler pass re-evaluating the pair, so a slow destination
    /// faces one retry stream rather than a backlog.
    /// </summary>
    /// <param name="job">The work.</param>
    /// <returns>
    /// <see langword="true"/> when queued; <see langword="false"/> when the
    /// identity is already active, or when the queue has stopped and will run
    /// nothing further.
    /// </returns>
    public bool Enqueue(QueuedJob job)
    {
        ThrowHelper.ThrowIfNull(job);

        string? victim = null;
        lock (_gate)
        {
            if (_disposed)
            {
                // A stopping queue takes no more work. Refusing here rather
                // than further down is what keeps the semaphores below from
                // being posted to after they are disposed — an
                // ObjectDisposedException raised on a background thread during
                // shutdown, which reads as a crash to everything above it.
                return false;
            }

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
                victim = MaybePreemptLocked(key);
            }
        }

        if (victim is not null)
        {
            _ = EscalateIfUnparkedAsync(victim);
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
    /// <remarks>
    /// Idempotent, for the same reason <see cref="ServiceRuntime.DisposeAsync"/>
    /// is: a host that stops the queue deliberately and then lets the runtime
    /// go disposes it twice, and the second call must be a no-op rather than an
    /// <see cref="ObjectDisposedException"/> thrown out of a shutdown path
    /// where nobody is left to handle it.
    /// </remarks>
    /// <returns>A task that completes when the workers have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

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

        // A parked run has no worker attending it, so awaiting the workers
        // alone would leave its task mid-cancellation. The snapshot happens
        // AFTER the workers exit: only workers move jobs into the paused
        // set, so nothing can slip in behind it.
        Task[] parked;
        lock (_gate)
        {
            parked = [.. _paused.Values.Select(entry => entry.RunTask).OfType<Task>()];
        }

        try
        {
            await Task.WhenAll(parked).ConfigureAwait(false);
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

    private const string PreemptedReason = "suspended for a higher-priority run";

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
                if (_backgroundHeld && !entry.Job.UserInitiated)
                {
                    continue;
                }

                if (bestPaused is null || entry.Key.CompareTo(bestPaused.Key) < 0)
                {
                    bestPaused = entry;
                }
            }

            // The lane's key sorts user-initiated first, so a background head
            // means every entry behind it is background too: one peek decides
            // the whole queue, with no dequeue-and-requeue and no second
            // structure. Without this the hold is half a limit — at the
            // default pool width of one, parking the running set hands its
            // worker straight to the next one queued.
            var hasQueued = _writerLane.TryPeek(out var head, out var queuedKey)
                && !(_backgroundHeld && !head.UserInitiated);
            if (bestPaused is not null && (!hasQueued || bestPaused.Key.CompareTo(queuedKey) <= 0))
            {
                _paused.Remove(bestPaused.Job.JobId);
                _attended[bestPaused.Job.JobId] = bestPaused;
                bestPaused.NeedsResume = true;
                work = bestPaused;
                return true;
            }

            if (hasQueued && _writerLane.TryDequeue(out var job, out var key))
            {
                if (_running.TryGetValue(job.JobId, out var source))
                {
                    var fresh = new WriterAttendance(job, key, source);
                    _attended[job.JobId] = fresh;
                    work = fresh;
                    return true;
                }

                // Unreachable while queued jobs stay tracked — and exactly the
                // silent-orphan hazard Scheduler.Enqueue guards its completion
                // against, so it is a log line, never a quiet discard.
                Log.QueuedJobUntracked(_log, job.JobId, job.Description);
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
        // next slot, weighed against the queue at every pickup. A job whose
        // supervision already closed out — cancelled in the instant between
        // the park signal and this lock — must NOT enter the paused set: a
        // ghost there would be resumed dead, and disposal would miss it.
        long parkGeneration;
        lock (_gate)
        {
            _attended.Remove(work.Job.JobId);
            if (!_running.ContainsKey(work.Job.JobId))
            {
                return;
            }

            parkGeneration = ++_parkSequence;
            work.ParkGeneration = parkGeneration;
            _paused[work.Job.JobId] = work;
        }

        _ = ExpireIfStillPausedAsync(work.Job.JobId, parkGeneration);
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
            // One lock for both removals: the park path checks _running under
            // the same gate before inserting into _paused, so a job can never
            // be parked and completed at once.
            lock (_gate)
            {
                _paused.Remove(job.JobId);
                if (_running.Remove(job.JobId, out var tracked))
                {
                    tracked.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Pauses the worst-ranked attended writer job when the incomer outranks
    /// it and no worker is free. Called under <see cref="_gate"/> from
    /// <see cref="Enqueue"/>; a gate whose job never reaches another pause
    /// point simply finishes instead — the request costs nothing.
    /// </summary>
    private string? MaybePreemptLocked((int Initiation, int Priority, long Arrival) incomer)
    {
        if (_attended.Count < _writerWorkers)
        {
            return null;
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
            victim.Job.PauseGate!.Pause(PreemptedReason);
            return victim.Job.JobId;
        }

        return null;
    }

    /// <summary>
    /// The ask moves on when unanswered (ADR-0047 Amendment 2): after the
    /// escalation window, a victim still attended and still merely ASKED —
    /// running, not parked — is stepped past, and the next-worst gated run
    /// is asked instead, so the incomer is never hostage to the one job
    /// that cannot reach a pause point. Chains until someone parks or the
    /// gated population runs out; a victim that parks later simply joins
    /// the paused set, resumed by rank like any other.
    /// </summary>
    private async Task EscalateIfUnparkedAsync(string victimId)
    {
        try
        {
            await Task.Delay(_escalationDelay, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string? next = null;
        lock (_gate)
        {
            if (!_attended.TryGetValue(victimId, out var entry)
                || entry.Job.PauseGate is not { IsPaused: true })
            {
                // Parked, finished, or the ask was withdrawn — no escalation.
                return;
            }

            if (_writerLane.TryPeek(out _, out var queuedKey))
            {
                next = MaybePreemptLocked(queuedKey);
            }
        }

        if (next is not null)
        {
            _ = EscalateIfUnparkedAsync(next);
        }
    }

    private async Task ExpireIfStillPausedAsync(string jobId, long parkGeneration)
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
            // The SAME park only: a run that resumed and re-parked inside
            // this window is younger than the bound, whatever this timer
            // thinks — its own park armed its own expiry.
            stillPaused = _paused.TryGetValue(jobId, out var entry)
                && entry.ParkGeneration == parkGeneration;
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

        /// <summary>
        /// Which park the max-pause expiry was armed for: a resume-and-re-park
        /// inside the first park's window must not be cancelled by the first
        /// park's timer.
        /// </summary>
        public long ParkGeneration { get; set; }
    }
}
