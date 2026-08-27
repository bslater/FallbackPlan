namespace FallbackPlan.Domain.Jobs;

/// <summary>
/// A cooperative suspension point (ADR-0047 Amendment 1). Long-running work calls
/// <see cref="WaitWhilePausedAsync"/> at its natural boundaries — the capture
/// pipeline does so between scan events, so a run parks between files, never
/// inside one — and the call returns immediately while nothing has asked the
/// work to pause. When the scheduler has, the call parks in memory until the
/// scheduler resumes it, with every accumulated state (scanner position,
/// open spool, catalogue session) held exactly where it was.
/// </summary>
/// <remarks>
/// The interface lives in Domain so the engine can honour a pause without
/// knowing the scheduler that requested it; the concrete gate, with the
/// pause/resume half the scheduler drives, lives with the scheduler.
/// Cancellation cuts through a parked call — the job's own token cancelling
/// throws <see cref="OperationCanceledException"/> out of the park, which is
/// how a paused run degrades to the ordinary interruption-safe re-run path
/// on shutdown.
/// </remarks>
public interface IPauseGate
{
    /// <summary>
    /// Returns immediately while not paused; parks until resumed otherwise.
    /// </summary>
    /// <param name="cancellationToken">The job's own token — cancelling it cuts through the park.</param>
    /// <returns>A task that completes when the work may proceed.</returns>
    ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken);
}
