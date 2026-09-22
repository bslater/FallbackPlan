using Bodu;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Agent;

/// <summary>
/// The scheduler's half of a job's suspension point (ADR-0047 Amendment 1). The job
/// checks in through <see cref="IPauseGate.WaitWhilePausedAsync"/> at its file
/// boundaries; the scheduler asks with <see cref="Pause"/>, learns the job
/// actually parked through <see cref="Parked"/> — the moment it may hand the
/// worker to someone else — and hands the slot back with <see cref="Resume"/>.
/// </summary>
/// <remarks>
/// <para>
/// A pause is a request, not a fact: between <see cref="Pause"/> and the
/// park the job is still running, and it may instead finish — which is why
/// the scheduler waits on <em>either</em> the job's task or
/// <see cref="Parked"/>, never on the park alone.
/// </para>
/// <para>
/// The callbacks carry the journal's view: <c>onParked</c> fires on the
/// job's own thread the moment it parks, <c>onResumed</c> when it wakes
/// again — and not when a parked job is cancelled instead, because that path
/// exits by exception into the ordinary cancellation transition.
/// </para>
/// <para>
/// A park carries the <em>reason</em> it was asked for, because there is more
/// than one asker: a higher-priority arrival (ADR-0047 Amendment 1) and a
/// background window that has shut (ADR-0069). The reason reaches
/// <c>onParked</c> and from there the journal, which is what a person reads
/// hours later to answer "why did my backup stop at ten". A single hard-coded
/// sentence would be a lie half the time, so <see cref="Pause"/> takes one
/// and has no default — a default is how the second asker forgets.
/// </para>
/// </remarks>
public sealed class PauseGate : IPauseGate
{
    private readonly Lock _lock = new();
    private readonly Action<string>? _onParked;
    private readonly Action? _onResumed;
    private List<Action<string>>? _alsoOnParked;
    private List<Action>? _alsoOnResumed;
    private TaskCompletionSource _parked = NewSignal();
    private TaskCompletionSource? _resume;
    private string _reason = string.Empty;

    /// <summary>Creates a gate, optionally observing park and resume.</summary>
    /// <param name="onParked">Runs on the job's thread the moment it parks, given the reason it was asked to.</param>
    /// <param name="onResumed">Runs on the job's thread when a parked job wakes.</param>
    public PauseGate(Action<string>? onParked = null, Action? onResumed = null)
    {
        _onParked = onParked;
        _onResumed = onResumed;
    }

    /// <summary>
    /// Registers a further pair of park/resume observers — the run's own
    /// progress reporter, beside the scheduler's journal callbacks fixed at
    /// construction (ADR-0047 Amendment 2: a suspension must reach progress
    /// watchers too, not only the journal).
    /// </summary>
    /// <param name="onParked">Runs on the job's thread the moment it parks, given the reason it was asked to.</param>
    /// <param name="onResumed">Runs on the job's thread when it wakes.</param>
    public void AddCallbacks(Action<string> onParked, Action onResumed)
    {
        ThrowHelper.ThrowIfNull(onParked);
        ThrowHelper.ThrowIfNull(onResumed);

        lock (_lock)
        {
            (_alsoOnParked ??= []).Add(onParked);
            (_alsoOnResumed ??= []).Add(onResumed);
        }
    }

    /// <summary>
    /// Completes when the job actually parks. Reset by <see cref="Resume"/>,
    /// so each pause cycle is its own signal.
    /// </summary>
    public Task Parked
    {
        get
        {
            lock (_lock)
            {
                return _parked.Task;
            }
        }
    }

    /// <summary>Whether a pause is currently requested.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _resume is not null;
            }
        }
    }

    /// <summary>
    /// Asks the job to park at its next boundary. Idempotent — and the first
    /// ask wins the reason, so a window closing over a run a higher-priority
    /// arrival had already asked for does not rewrite what the journal will
    /// say happened.
    /// </summary>
    /// <param name="reason">Why, in the words the journal and the progress surface will carry.</param>
    public void Pause(string reason)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(reason);

        lock (_lock)
        {
            if (_resume is null)
            {
                _resume = NewSignal();
                _reason = reason;
            }
        }
    }

    /// <summary>Wakes a parked job and re-arms <see cref="Parked"/>. Idempotent.</summary>
    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_lock)
        {
            resume = _resume;
            _resume = null;
            if (_parked.Task.IsCompleted)
            {
                _parked = NewSignal();
            }
        }

        resume?.TrySetResult();
    }

    /// <inheritdoc/>
    public async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A loop, not an if: a job can be paused again in the instant after
        // it resumed, and each cycle announces its own park.
        while (true)
        {
            Task resumeTask;
            TaskCompletionSource parkedSignal;
            string reason;
            lock (_lock)
            {
                if (_resume is null)
                {
                    return;
                }

                resumeTask = _resume.Task;
                parkedSignal = _parked;
                reason = _reason;
            }

            if (parkedSignal.TrySetResult())
            {
                _onParked?.Invoke(reason);
                InvokeAll(_alsoOnParked, reason);
            }

            await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            _onResumed?.Invoke();
            InvokeAll(_alsoOnResumed);
        }
    }

    private void InvokeAll(List<Action<string>>? callbacks, string reason)
    {
        Action<string>[] snapshot;
        lock (_lock)
        {
            if (callbacks is null || callbacks.Count == 0)
            {
                return;
            }

            snapshot = [.. callbacks];
        }

        foreach (var callback in snapshot)
        {
            callback(reason);
        }
    }

    private void InvokeAll(List<Action>? callbacks)
    {
        Action[] snapshot;
        lock (_lock)
        {
            if (callbacks is null || callbacks.Count == 0)
            {
                return;
            }

            snapshot = [.. callbacks];
        }

        foreach (var callback in snapshot)
        {
            callback();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
