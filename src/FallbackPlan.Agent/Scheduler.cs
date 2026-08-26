using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using Microsoft.Extensions.Logging;
using System.Globalization;

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
    /// <summary>
    /// The pass's transfer work — fan-out and sweeps — still running when the
    /// pass answered (ADR-0029 Amendment 4). The pass no longer waits for
    /// the transfer lane: a multi-hour copy used to mean no pass ran at all,
    /// so due-ness was never evaluated and every set's scheduled
    /// incrementals silently stopped. <c>--once</c> and the tests await
    /// this before tearing the runtime down; the service deliberately does
    /// not, and the stable per-pair job identities are what keep un-awaited
    /// passes from piling work up. Never faults — the phases guard their own
    /// exceptions, exactly as they did when the pass awaited them inline.
    /// </summary>
    public Task Transfers { get; init; } = Task.CompletedTask;

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

        // The category names where the records come from, and this pass is a
        // static class — which the generic overload cannot express, and which
        // is why the analyzer's suggestion does not apply here.
#pragma warning disable CA2263
        var pass = runtime.LoggerFor(typeof(Scheduler));
#pragma warning restore CA2263

        // Read once per pass through the logged path, so "what was in force
        // when this ran" is answerable afterwards — and so the loop below
        // works from one snapshot rather than re-reading per set.
        var configuration = runtime.LoadConfiguration();


        foreach (var set in configuration.BackupSets)
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

            // One run per set at a time (ADR-0027 §1). This was structural
            // while the pass was serial — it never looked again before its
            // own captures finished. A pass that ticks during long captures
            // (ADR-0029 Amendment 4) needs the rule stated, or every tick
            // would queue another run behind a slow one.
            var latest = runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == set.Id);
            if (latest is not null && !HasSettled(latest.State) && runtime.Queue.IsActive(latest.Id))
            {
                outcomes.Add(new AgentSetOutcome(
                    set.Name, "already-running", $"job {latest.Id} is still queued or running"));
                continue;
            }

            var anchor = runtime.Jobs.ScheduleAnchor(set.Id);

            if (!schedule!.IsDue(anchor, now))
            {
                var next = schedule.NextRun(anchor, now);
                if (pass.IsEnabled(LogLevel.Debug))
                {
                    var nextRun = next.ToString("u", CultureInfo.InvariantCulture);
                    Log.SetNotDue(pass, set.Name, nextRun);
                }

                outcomes.Add(new AgentSetOutcome(set.Name, "not-due", $"next: {next:u}"));
                continue;
            }

            // The answer to "why did this not run" and "why did this run now",
            // which is asked hours after anybody could have watched it happen.
            // Formatted into locals inside the guard: CA1873 does not read
            // IsEnabled, and it is right that an argument expression is
            // evaluated whether or not anybody is listening.
            if (pass.IsEnabled(LogLevel.Debug))
            {
                var lastCompleted = anchor?.ToString("u", CultureInfo.InvariantCulture) ?? "never";
                var nextRun = schedule.NextRun(anchor, now).ToString("u", CultureInfo.InvariantCulture);
                Log.SetDue(pass, set.Name, lastCompleted, nextRun);
            }

            running.Add(Enqueue(runtime, set, now, userInitiated: false));
        }

        foreach (var outcome in await Task.WhenAll(running).ConfigureAwait(false))
        {
            outcomes.Add(new AgentSetOutcome(outcome.SetName, outcome.Outcome, outcome.Detail));
        }

        // Phases 2 and 3 run WITHOUT holding the pass hostage (ADR-0029
        // Amendment 4): the returned task is handed to the caller instead of
        // awaited here, so a multi-hour transfer no longer stops due-ness
        // being evaluated — which used to silently swallow every set's
        // scheduled cadence for the copy's whole duration. --once awaits it;
        // the service does not, and the stable per-pair job identities keep
        // un-awaited passes from piling work up (the duplicate enqueue is
        // refused, and the NEXT pass re-evaluates the pair).
        return new AgentPassResult(outcomes)
        {
            Transfers = RunTransferPhasesAsync(runtime, now, cancellationToken),
        };
    }

    /// <summary>
    /// Phase 2, fan-out (ADR-0034 §3): after the backups, so a fresh snapshot
    /// reaches its destinations promptly; and every pass, so a destination
    /// that was offline catches up under back-off with no operator action
    /// (FR-DEST-003) — the pass is the retry pump, there is no other timer.
    /// Then phase 3, the deep sweep (FR-VER-002), strictly after the fan-out
    /// completes: a sweep that read a replica while convergence was putting
    /// and deleting in it would manufacture failures about damage that never
    /// existed. Never throws — one faulted destination must never take the
    /// scheduler loop, or an un-awaiting caller, down.
    /// </summary>
    private static async Task RunTransferPhasesAsync(
        ServiceRuntime runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var syncs = new List<Task>();
        foreach (var set in runtime.Configuration.BackupSets)
        {
            if (!runtime.ArchiveExists(set.Id))
            {
                continue;
            }

            foreach (var reference in set.Destinations)
            {
                if (ShouldSync(runtime, set, reference.Ref, now)
                    && FanOut.Enqueue(runtime, set, reference.Ref, now, userInitiated: false) is { } sync)
                {
                    syncs.Add(sync);
                }
            }
        }

        // The wait honours shutdown: a stop signal mid-transfer must reach
        // the runtime's disposal — which is what cancels the jobs — rather
        // than sit behind an hours-long fan-out until the service manager
        // gives up and kills the process.
        try
        {
            await Task.WhenAll(syncs).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The syncs themselves resume on the next pass; each object
            // committed whole or not at all.
            return;
        }
        catch (Exception)
        {
            // A sync that faulted past its own handlers is the lane worker's
            // to log and the ledger's to carry; the next pass retries the
            // pair under back-off.
        }

        var sweeps = new List<Task>();
        foreach (var set in runtime.Configuration.BackupSets)
        {
            if (!runtime.ArchiveExists(set.Id))
            {
                continue;
            }

            foreach (var reference in set.Destinations)
            {
                if (ShouldSweep(runtime, set, reference.Ref, now)
                    && ReplicaSweepJob.Enqueue(runtime, set, reference.Ref, now, userInitiated: false) is { } sweep)
                {
                    sweeps.Add(sweep);
                }
            }
        }

        try
        {
            await Task.WhenAll(sweeps).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled segment simply does not advance its cursor; the next
            // pass re-reads from where the last completed one left off.
        }
        catch (Exception)
        {
            // As with fan-out: one destination's sweep faulting past its own
            // handlers must never take anything down with it.
        }
    }

    /// <summary>Whether a journal state is finished — the one-run-per-set rule's input.</summary>
    private static bool HasSettled(JobState state) => state is
        JobState.Complete
        or JobState.CompletedWithFailures
        or JobState.Cancelled
        or JobState.FailedRecoverable
        or JobState.FailedPermanent;

    /// <summary>
    /// Whether a (set, destination) pair is due another sweep segment.
    /// </summary>
    /// <remarks>
    /// Only local-path destinations sweep: a peer replica is behind the wire
    /// with no store to read, so confirming its bytes needs the range
    /// challenge, not this. Deliberately stated rather than silently skipped.
    /// </remarks>
    private static bool ShouldSweep(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DateTimeOffset now)
    {
        if (runtime.Configuration.FindDestination(destinationName) is not
            { Kind: DestinationKind.LocalPath } destination)
        {
            return false;
        }

        var record = runtime.DestinationSync.Find(set.Id, destinationName);
        if (record?.LastSuccessAt is null)
        {
            // Nothing has been copied there yet; there is nothing to re-read.
            return false;
        }

        if (record.SweptAt is not { } swept)
        {
            return true;
        }

        var interval = (ulong)(destination.DeepVerifyIntervalDays ?? ReplicaSweepJob.DefaultIntervalDays)
            * 24UL * 3_600_000UL;
        return (ulong)now.ToUnixTimeMilliseconds() >= swept + interval;
    }

    /// <summary>
    /// Whether a (set, destination) pair warrants an attempt this pass: yes
    /// when never tried, when the staging archive moved past the last success,
    /// and when a previous failure's back-off has elapsed. An in-sync pair
    /// with nothing new costs nothing; a stated incapacity is refreshed only
    /// once per new snapshot rather than retried.
    /// </summary>
    private static bool ShouldSync(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DateTimeOffset now)
    {
        var record = runtime.DestinationSync.Find(set.Id, destinationName);
        if (record is null)
        {
            return true;
        }

        // A migrating direct-ship set (ADR-0046) keeps syncing while its
        // staging archive remains: a run's ledger success says the run's own
        // objects shipped, not that the history only staging still holds has
        // reached anyone — and the catch-up copy through the sink is what
        // carries it out. Cheap once converged (an inventory diff), gone
        // once retire_staging deletes the archive. Failures still back off
        // through the ordinary arm below.
        if (set.DirectShip
            && record.State == DestinationSyncState.InSync
            && File.Exists(Path.Combine(
                runtime.ArchivePath(set.Id), Repository.RepositoryLifecycle.DescriptorKey.Value)))
        {
            return true;
        }

        var lastCompleted = runtime.Jobs.LastCompleted(set.Id)?.UpdatedAt ?? 0;
        var behind = record.LastSuccessAt is null || record.LastSuccessAt < lastCompleted;

        return record.State switch
        {
            DestinationSyncState.InSync => behind,
            DestinationSyncState.NotSupported => record.LastAttemptAt < lastCompleted,

            // Unavailable and failed retry under exponential back-off, capped
            // at an hour, anchored to the poll cadence — the gap closes itself
            // when the destination returns (FR-DEST-003), without hammering a
            // drive that is simply unplugged for the week.
            _ => (ulong)now.ToUnixTimeMilliseconds() >= record.LastAttemptAt + BackoffMs(runtime, record.ConsecutiveFailures),
        };
    }

    private static ulong BackoffMs(ServiceRuntime runtime, int consecutiveFailures) =>
        Math.Min((ulong)runtime.Options.PollSeconds * (1UL << Math.Min(consecutiveFailures, 6)), 3_600UL) * 1_000UL;

    /// <summary>
    /// Queues one set's backup and hands back a task that completes when it
    /// does — so a caller that must wait can, and the service, which must not,
    /// need not.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set to back up.</param>
    /// <param name="now">The clock.</param>
    /// <param name="userInitiated">Whether a person is waiting for it.</param>
    /// <param name="full">Whether to ignore prior versions and re-capture everything.</param>
    /// <returns>The job's outcome, when it finishes.</returns>
    public static Task<BackupOutcome> Enqueue(
        ServiceRuntime runtime, BackupSetConfiguration set, DateTimeOffset now, bool userInitiated,
        bool full = false)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);

        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var completion = new TaskCompletionSource<BackupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var accepted = runtime.Queue.Enqueue(new QueuedJob(
            job.Id,
            JobLane.Writer,
            userInitiated,
            $"backup {set.Name}",
            async cancellationToken =>
            {
                try
                {
                    completion.SetResult(
                        await BackupRunner.RunAsync(runtime, set, job.Id, now, full, cancellationToken)
                            .ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                    throw;
                }
            },
            Priority: set.Priority ?? 0));

        if (!accepted)
        {
            // Unreachable while job identities are fresh GUIDs — but a refusal
            // that silently orphaned the completion would hang whoever awaits
            // it, forever, the day that ever changes. The journal row is
            // closed for the same reason: a Pending row nothing runs is a
            // stuck job in every listing.
            runtime.Jobs.Transition(
                job.Id, JobState.Cancelled, (ulong)now.ToUnixTimeMilliseconds(),
                "refused by the queue: a job with this identity is already active");
            completion.SetResult(new BackupOutcome(
                set.Name, "already-running", $"job {job.Id} is already queued or running"));
        }

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
