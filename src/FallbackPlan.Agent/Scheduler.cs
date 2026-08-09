using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Agent;

/// <summary>One set's outcome within a pass.</summary>
/// <param name="SetName">The set.</param>
/// <param name="Outcome">One of <c>ran</c>, <c>not-due</c>, <c>manual-only</c>, <c>failed</c>, <c>cancelled</c>.</param>
/// <param name="Detail">What to tell the operator.</param>
public sealed record AgentSetOutcome(string SetName, string Outcome, string? Detail);

/// <summary>What one scheduler pass did.</summary>
/// <param name="Sets">One entry per configured set.</param>
public sealed record AgentPassResult(IReadOnlyList<AgentSetOutcome> Sets)
{
    /// <summary>Sets that ran a backup this pass.</summary>
    public int Ran => Sets.Count(set => set.Outcome == "ran");

    /// <summary>Sets skipped as not due or unscheduled.</summary>
    public int Skipped => Sets.Count(set => set.Outcome is "not-due" or "manual-only");

    /// <summary>Sets that failed this pass.</summary>
    public int Failed => Sets.Count(set => set.Outcome == "failed");
}

/// <summary>
/// Decides which sets are due and queues them (ADR-0027 §1).
/// </summary>
/// <remarks>
/// The pass asks "is a run due", never "how many were missed", which is what
/// makes missed-run coalescing structural rather than arithmetic: five
/// slept-through times cannot become five owed runs, because the answer is a
/// boolean about a single instant.
/// </remarks>
public static class Scheduler
{
    /// <summary>Evaluates every configured set and queues the due ones.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="now">The clock, passed in so the derivation stays pure.</param>
    /// <param name="cancellationToken">Cancels the wait for queued work.</param>
    /// <returns>What happened to each set.</returns>
    public static async ValueTask<AgentPassResult> RunPassAsync(
        ServiceRuntime runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(runtime);

        var outcomes = new List<AgentSetOutcome>();
        var running = new List<Task<BackupOutcome>>();

        foreach (var set in runtime.Configuration.BackupSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(set.Schedule))
            {
                outcomes.Add(new AgentSetOutcome(set.Name, "manual-only", null));
                continue;
            }

            if (!Schedule.TryParse(set.Schedule, out var schedule, out var defect))
            {
                // A misconfigured schedule needs a human — permanent, never
                // silently retried (10 §3).
                var broken = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
                runtime.Jobs.Transition(
                    broken.Id, JobState.FailedPermanent, (ulong)now.ToUnixTimeMilliseconds(), defect);
                outcomes.Add(new AgentSetOutcome(set.Name, "failed", defect));
                continue;
            }

            var anchor = runtime.Jobs.LastCompleted(set.Id) is { } completed
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)completed.UpdatedAt)
                : (DateTimeOffset?)null;

            if (!schedule!.IsDue(anchor, now))
            {
                outcomes.Add(new AgentSetOutcome(set.Name, "not-due", $"next: {schedule.NextRun(anchor, now):u}"));
                continue;
            }

            running.Add(Enqueue(runtime, set, now, userInitiated: false));
        }

        foreach (var outcome in await Task.WhenAll(running).ConfigureAwait(false))
        {
            outcomes.Add(new AgentSetOutcome(outcome.SetName, outcome.Outcome, outcome.Detail));
        }

        return new AgentPassResult(outcomes);
    }

    /// <summary>
    /// Queues one set's backup and hands back a task that completes when it
    /// does — so a caller that must wait can, and the service, which must not,
    /// need not.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set to back up.</param>
    /// <param name="now">The clock.</param>
    /// <param name="userInitiated">Whether a person is waiting for it.</param>
    /// <returns>The job's outcome, when it finishes.</returns>
    public static Task<BackupOutcome> Enqueue(
        ServiceRuntime runtime, BackupSetConfiguration set, DateTimeOffset now, bool userInitiated)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);

        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var completion = new TaskCompletionSource<BackupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        runtime.Queue.Enqueue(new QueuedJob(
            job.Id,
            JobLane.Writer,
            userInitiated,
            $"backup {set.Name}",
            async cancellationToken =>
            {
                try
                {
                    completion.SetResult(
                        await BackupRunner.RunAsync(runtime, set, job.Id, now, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                    throw;
                }
            }));

        return completion.Task;
    }

    /// <summary>The identity of the job most recently begun for a set, if any.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="backupSetId">The set's identity.</param>
    /// <returns>The job identity, or null.</returns>
    public static string? LatestJobFor(ServiceRuntime runtime, string backupSetId)
    {
        ThrowHelper.ThrowIfNull(runtime);
        return runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == backupSetId)?.Id;
    }
}
