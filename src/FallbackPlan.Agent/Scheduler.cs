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

    /// <summary>
    /// The pass's restore drills (ADR-0054), still running when the pass
    /// answered. Separate from <see cref="Transfers"/> and strictly after it:
    /// a drill reads a replica, and reading one mid-convergence would report
    /// damage that never existed — the same rule the deep sweep follows.
    /// Never faults.
    /// </summary>
    public Task Drills { get; init; } = Task.CompletedTask;

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
    /// <param name="userInitiated">
    /// Whether a person asked for this pass. A scheduled tick is the service's
    /// and observes the background window (NFR-PERF-013, ADR-0069); a person
    /// who typed <c>run --once</c> is not background activity, and gating them
    /// would be the same mistake as making a restore wait for a backup
    /// (ADR-0029 §4).
    /// </param>
    /// <returns>What happened to each set.</returns>
    public static async ValueTask<AgentPassResult> RunPassAsync(
        ServiceRuntime runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool userInitiated = false)
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

        // The window is read from the configuration this pass loaded, not
        // pinned at service start like the pool width: a limit whose whole
        // point is that it changes during the day would be useless fixed at
        // boot. Absent means any hour, which is what every file written
        // before schema 6 says by not mentioning it.
        var window = userInitiated ? null : configuration.EffectiveBackgroundWindow;
        var shut = window is not null && !window.IsOpen(now);
        if (shut && pass.IsEnabled(LogLevel.Information))
        {
            // Formatted into a local inside the guard: CA1873 does not read
            // IsEnabled, and it is right that an argument expression is
            // evaluated whether or not anybody is listening.
            var opens = window!.NextOpen(now).ToString("u", CultureInfo.InvariantCulture);
            Log.BackgroundWindowShut(pass, window.Text, opens);
        }

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
            // would queue another run behind a slow one. Enqueue enforces
            // the same rule for every caller; checking here first keeps the
            // per-set outcome row and skips the call.
            var latest = runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == set.Id);
            if (latest is not null && !HasSettled(latest.State) && runtime.Queue.IsActive(latest.Id))
            {
                outcomes.Add(new AgentSetOutcome(
                    set.Name, "already-running", $"job {latest.Id} is still queued or running"));
                continue;
            }

            // Asked after due-ness would have been evaluated but before the
            // work is queued, so the row says "due, and held" rather than
            // silently reading as not due — "why did nothing run" is the
            // question a window creates, and this is where it is answered.
            if (shut)
            {
                outcomes.Add(new AgentSetOutcome(
                    set.Name,
                    "outside-window",
                    $"the background window {window!.Text} is shut; it opens {window.NextOpen(now):u}"));
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
        // Fan-out, the deep sweep and the drills are background activity by
        // the same definition the captures are — the scheduler starts them
        // with nobody waiting — so a shut window holds all four rather than
        // only the one an operator would notice.
        var transfers = shut ? Task.CompletedTask : RunTransferPhasesAsync(runtime, now, cancellationToken);
        return new AgentPassResult(outcomes)
        {
            Transfers = transfers,
            Drills = shut ? Task.CompletedTask : RunDrillPhaseAsync(runtime, transfers, now, cancellationToken),
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

    /// <summary>
    /// Phase 4, the restore drills (ADR-0054): after the transfers, because a
    /// drill reads a replica and convergence writes and deletes in one. Never
    /// throws — a check that takes the scheduler down is worse than the
    /// condition it went looking for.
    /// </summary>
    /// <remarks>
    /// One pair at a time, deliberately. A drill rebuilds a catalogue and
    /// writes real bytes to disk; running every configured pair's at once
    /// would turn the rarest job in the service into its heaviest moment.
    /// </remarks>
    private static async Task RunDrillPhaseAsync(
        ServiceRuntime runtime, Task transfers, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await transfers.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transfer phases guard their own failures; this await is for
            // ordering, not for outcome.
        }

        foreach (var set in runtime.Configuration.BackupSets)
        {
            if (!runtime.ArchiveExists(set.Id))
            {
                continue;
            }

            foreach (var reference in set.Destinations)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!ShouldDrill(runtime, set, reference.Ref, now))
                {
                    continue;
                }

                try
                {
                    await RecoveryDrillJob.RunAsync(
                        runtime, set, reference.Ref, (ulong)now.ToUnixTimeMilliseconds(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // RunAsync records its own failures; anything past it is
                    // one destination's problem and never the pass's.
                }
            }
        }
    }

    /// <summary>
    /// Whether a (set, destination) pair is due a restore drill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A local path drills on a default cadence. A peer drills only on a
    /// cadence the source's operator wrote down for it: its replica is
    /// behind the wire, and a drill reads it over the retrieval session the
    /// peer granted and spends the peer's bandwidth — a standing cost this
    /// service must not put on somebody else's link by default
    /// ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)
    /// Amendment 3). Absent means never, and the bytes one drill may pull
    /// are capped in <see cref="RecoveryDrillJob"/>.
    /// </para>
    /// <para>
    /// A pair nothing has ever reached is not due one: there is nothing there
    /// to restore, and drilling would manufacture a failure about an absence
    /// that is correct.
    /// </para>
    /// </remarks>
    private static bool ShouldDrill(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DateTimeOffset now)
    {
        if (runtime.Configuration.FindDestination(destinationName) is not { } destination)
        {
            return false;
        }

        switch (destination.Kind)
        {
            case DestinationKind.LocalPath:
                break;

            case DestinationKind.Peer when destination.DrillIntervalDays is not null:
                break;

            default:
                return false;
        }

        var record = runtime.DestinationSync.Find(set.Id, destinationName);
        if (record?.LastSuccessAt is null)
        {
            return false;
        }

        if (record.DrilledAt is not { } drilled)
        {
            return true;
        }

        var interval = (ulong)(destination.DrillIntervalDays ?? RecoveryDrillJob.DefaultIntervalDays)
            * 24UL * 3_600_000UL;
        return (ulong)now.ToUnixTimeMilliseconds() >= drilled + interval;
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
            // Verification rides the sync path, so a pair with nothing to copy
            // used to go unchallenged for ever — and a direct-ship set
            // (ADR-0046) has nothing to copy the moment it converges, because
            // its capture wrote straight to the destination. That made
            // FR-VER-002's "sample per interval" mean "sample whenever a copy
            // happened to be due", which for the default shape of a new
            // local-path set was never. The pass is cheap when there is
            // nothing to carry — an inventory diff — so being due a challenge
            // is reason enough to run it.
            DestinationSyncState.InSync => behind || ShouldChallenge(record, now),

            // A run held the pair out for missing history (ADR-0046 §3's
            // scope rule): the catch-up IS the heal, so it runs at once —
            // backing off here would delay the very copy the skip waits on.
            DestinationSyncState.Behind => true,
            DestinationSyncState.NotSupported => record.LastAttemptAt < lastCompleted,

            // Unavailable and failed retry under exponential back-off, capped
            // at an hour, anchored to the poll cadence — the gap closes itself
            // when the destination returns (FR-DEST-003), without hammering a
            // drive that is simply unplugged for the week.
            _ => (ulong)now.ToUnixTimeMilliseconds() >= record.LastAttemptAt + BackoffMs(runtime, record.ConsecutiveFailures),
        };
    }

    /// <summary>
    /// How long a destination's proof of possession stays good before the
    /// pair is due another (FR-VER-002).
    /// </summary>
    /// <remarks>
    /// Six hours rather than the deep sweep's days because the two ask
    /// different questions at very different prices: the sweep re-reads a
    /// whole replica, while a challenge samples a bounded handful of objects.
    /// Verification is reported as coverage AND age (FR-VER-003), and an age
    /// measured in weeks makes the second half of that pair meaningless.
    /// </remarks>
    private const ulong ChallengeIntervalMs = 6UL * 3_600_000UL;

    /// <summary>
    /// Whether a pair is due a possession challenge: a destination that has
    /// never been challenged is due now, and one that has is due again when
    /// its proof has aged past <see cref="ChallengeIntervalMs"/>.
    /// </summary>
    /// <remarks>
    /// A pair nothing has ever been copied to is not due one — there is
    /// nothing there to prove, and asking would manufacture a failure about
    /// an absence that is correct.
    /// </remarks>
    private static bool ShouldChallenge(DestinationSyncRecord record, DateTimeOffset now) =>
        record.LastSuccessAt is not null
        && (record.VerifiedAt is not { } verified
            || (ulong)now.ToUnixTimeMilliseconds() >= verified + ChallengeIntervalMs);

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

        var completion = new TaskCompletionSource<BackupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (runtime.BackupEnqueueGate)
        {
            // One run per set at a time (ADR-0027 §1), enforced at the one
            // door every trigger funnels through — the pass, the manual
            // command, the upsert's first backup. The pass's own pre-check
            // is a courtesy that avoids the call; this is the guarantee. It
            // matters because two runs of one set share a spool directory
            // and a writer sequence, whose invariants assume a single
            // session (ADR-0047 Amendment 3). The IsActive clause keeps an
            // unsettled journal row a previous process left behind from
            // blocking the set forever: only a job the live queue still
            // knows counts as running.
            var latest = runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == set.Id);
            if (latest is not null && !HasSettled(latest.State) && runtime.Queue.IsActive(latest.Id))
            {
                return Task.FromResult(new BackupOutcome(
                    set.Name, "already-running", $"job {latest.Id} is still queued or running"));
            }

            var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());

            // The run's suspension point (ADR-0047 Amendment 1): when a higher-priority
            // job needs the pool's last slot, the scheduler parks this run at a
            // file boundary and the journal says so — Paused on the way down,
            // back to Publishing on the way up. The stamps are wall-clock
            // because they record when the suspension actually happened, not
            // when the pass that queued the run began.
            var gate = new PauseGate(
                onParked: () => runtime.Jobs.Transition(
                    job.Id, JobState.Paused, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "suspended for a higher-priority run"),
                onResumed: () => runtime.Jobs.Transition(
                    job.Id, JobState.Publishing, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "resumed"));

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
                            await BackupRunner.RunAsync(runtime, set, job.Id, now, full, gate, cancellationToken)
                                .ConfigureAwait(false));
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                        throw;
                    }
                },
                Priority: set.Priority ?? 0,
                PauseGate: gate));

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
