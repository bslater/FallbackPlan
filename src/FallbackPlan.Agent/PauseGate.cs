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
/// </remarks>
public sealed class PauseGate : IPauseGate
{
    private readonly Lock _lock = new();
    private readonly Action? _onParked;
    private readonly Action? _onResumed;
    private TaskCompletionSource _parked = NewSignal();
    private TaskCompletionSource? _resume;

    /// <summary>Creates a gate, optionally observing park and resume.</summary>
    /// <param name="onParked">Runs on the job's thread the moment it parks.</param>
    /// <param name="onResumed">Runs on the job's thread when a parked job wakes.</param>
    public PauseGate(Action? onParked = null, Action? onResumed = null)
    {
        _onParked = onParked;
        _onResumed = onResumed;
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

    /// <summary>Asks the job to park at its next boundary. Idempotent.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _resume ??= NewSignal();
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
            lock (_lock)
            {
                if (_resume is null)
                {
                    return;
                }

                resumeTask = _resume.Task;
                parkedSignal = _parked;
            }

            if (parkedSignal.TrySetResult())
            {
                _onParked?.Invoke();
            }

            await resumeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            _onResumed?.Invoke();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
