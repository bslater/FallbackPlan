using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

/// <summary>
/// Runs one segment of a replica's deep sweep as a queued job: re-read the next
/// bounded run of stored blobs and confirm they are still what was sealed
/// (FR-VER-002, ADR-0034 §5).
/// </summary>
/// <remarks>
/// <para>
/// On the <b>transfer</b> lane, with fan-out, and not the reader lane despite
/// that lane's name mentioning verification. A sweep reads a replica; a
/// convergence writes and deletes in the same replica; the reader lane runs
/// alongside the transfer lane by design. Sweeping there would read a replica
/// mid-convergence and report damage that never existed — and a verification
/// failure sets the pair <c>Failed</c> and raises a durable notice, so a false
/// one is expensive to unsay.
/// </para>
/// <para>
/// The transfer lane has one worker for the whole process, which is what makes
/// the per-segment budget load-bearing rather than tidy: an unbounded sweep of
/// a large archive would stall fan-out to every destination of every set for as
/// long as it took.
/// </para>
/// </remarks>
internal static class ReplicaSweepJob
{
    /// <summary>
    /// Days between one finished circuit and the start of the next, when a
    /// destination states no preference. A circuit that has begun is carried
    /// on every pass until it closes; the interval rests between circuits,
    /// never between segments (ADR-0035 Amendment 1).
    /// </summary>
    public const int DefaultIntervalDays = 7;

    /// <summary>
    /// How long a background segment of a limited destination reads for: a
    /// segment holds the process's one transfer worker while it reads, so
    /// under a limit it reads about this long at the limit's rate.
    /// </summary>
    private const long SegmentSeconds = 60;

    /// <summary>
    /// Blobs per segment. A test hook scoped to the flow that sets it, as
    /// <see cref="FanOut.ReadBackBudget"/> is: a runtime started after it is
    /// set reads its segments at this size, and one anywhere else still reads
    /// the default.
    /// </summary>
    internal static int SegmentBudget
    {
        get => SegmentBudgetInFlow.Value ?? ReplicaSweep.DefaultBudget;
        set => SegmentBudgetInFlow.Value = value;
    }

    private static readonly AsyncLocal<int?> SegmentBudgetInFlow = new();

    /// <summary>The job identity, distinct from the pair's sync job so the two never displace each other.</summary>
    public static string JobIdFor(string setId, string destinationName) =>
        $"sweep-{setId}-{destinationName}";

    /// <summary>Queues one segment; null when a segment for this pair is already queued or running.</summary>
    public static Task? Enqueue(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        DateTimeOffset now,
        bool userInitiated)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = runtime.Queue.Enqueue(new QueuedJob(
            JobIdFor(set.Id, destinationName),
            JobLane.Transfer,
            userInitiated,
            $"verify {set.Name} -> {destinationName}",
            async token => await RunAsync(
                    runtime, set, destinationName, (ulong)now.ToUnixTimeMilliseconds(), userInitiated, token)
                .ConfigureAwait(false),
            // As the pair's sync is: answered once the queue has let the
            // segment's identity go, so the next segment can be asked for at
            // once.
            OnSettled: () => completion.TrySetResult()));

        return queued ? completion.Task : null;
    }

    /// <summary>
    /// Reads segments until the circuit closes, and reports what it found.
    /// </summary>
    /// <remarks>
    /// The on-demand full pass (FR-VER-004): a recovery drill wants "every
    /// object, now", not "the next sixty-four". It re-enters the same segment
    /// logic rather than a second implementation, so the two cannot disagree
    /// about what counts as damage.
    /// </remarks>
    public static async Task<(int Examined, int Damaged, int Repaired)> RunFullAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        var examined = 0;
        var damaged = 0;
        var repaired = 0;

        // Bounded by the number of segments a circuit can take, not by trust
        // that one will close: a cursor that somehow failed to advance must
        // end the loop rather than spin.
        string? previousCursor = null;
        for (var segment = 0; segment < 1_000_000; segment++)
        {
            // The on-demand full pass is a person's (FR-VER-004), so it reads
            // through no limit.
            var outcome = await RunAsync(runtime, set, destinationName, nowMs, userInitiated: true, cancellationToken)
                .ConfigureAwait(false);
            examined += outcome.Examined;
            damaged += outcome.Damaged;
            repaired += outcome.Repaired;

            if (outcome.CompletedCircuit || outcome.Cursor is null || outcome.Cursor == previousCursor)
            {
                break;
            }

            previousCursor = outcome.Cursor;
        }

        return (examined, damaged, repaired);
    }

    /// <summary>Reads the next segment and records what it found.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to sweep.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The pass clock, Unix milliseconds.</param>
    /// <param name="userInitiated">
    /// Whether a person is waiting. A background segment reads the replica
    /// through the destination's transfer limit (NFR-PERF-013, ADR-0074).
    /// </param>
    /// <param name="cancellationToken">Cancels the segment.</param>
    public static async Task<(int Examined, int Damaged, int Repaired, string? Cursor, bool CompletedCircuit)> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        bool userInitiated,
        CancellationToken cancellationToken)
    {
        if (runtime.Configuration.FindDestination(destinationName) is not
            { Kind: DestinationKind.LocalPath } destination)
        {
            return (0, 0, 0, null, false);
        }

        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return (0, 0, 0, null, false);
        }

        var replicaRoot = Path.Combine(destination.Path!, archive.Repository.RepositoryId.ToString());
        if (!Directory.Exists(replicaRoot))
        {
            // Unreachable right now. Fan-out owns saying so — its shortfall and
            // availability signals already cover a replica that has gone, and
            // a second voice saying it would be a second notice to acknowledge.
            return (0, 0, 0, null, false);
        }

        // The set gate, for the same reason fan-out takes it: a retention apply
        // mutates staging, and the length comparison reads staging.
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = runtime.DestinationSync;
            var previous = ledger.Find(set.Id, destinationName);

            // In-memory progress only. A journal entry would be keyed by set,
            // and the scheduler reads the journal's last-completed back as the
            // BACKUP anchor — a sweep writing there would move the next
            // backup's due-ness. So `Verifying` is reported and not recorded,
            // which is also why nothing polls for it to settle.
            runtime.Progress.Report(new JobProgress(
                JobIdFor(set.Id, destinationName), JobState.Verifying, 0, 0, 0, 0, 0, 0));

            var limiter = userInitiated ? null : runtime.Pacing.ForDestination(destination);
            var replica = PacedObjectStore.Over(StoreComposition.OpenLocal(replicaRoot), limiter);
            var result = await ReplicaSweep.RunAsync(
                archive.Repository.RepositoryId,
                archive.Repository.Keys,
                replica,
                archive.Store,
                previous?.SweepCursor,
                SegmentBudget,
                limiter is null ? ReplicaSweep.DefaultByteBudget : Math.Max(1, limiter.Rate.BytesPerSecond * SegmentSeconds),
                cancellationToken).ConfigureAwait(false);

            if (result.Findings.Count > 0)
            {
                // FR-VER-005: a verification failure degrades the pair and
                // raises a warning requiring action. The cursor still advances
                // — a damaged blob must not park the sweep on itself forever,
                // re-reporting the same object while the rest goes unchecked.
                // The keys go on the ledger before anything is done about
                // them, so a repair cut short leaves them to the next sync
                // rather than forgotten (FR-VER-007).
                ledger.RecordSweep(
                    set.Id, destinationName, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);
                ledger.RecordDamage(set.Id, destinationName, result.DamagedKeys, resolved: [], nowMs);

                IReadOnlyList<ReplicaRepairOutcome> outcomes;
                await using (var repairer = new ReplicaRepairer(runtime, set, destinationName, archive, userInitiated))
                {
                    outcomes = await repairer.RepairAsync(replica, result.DamagedKeys, cancellationToken)
                        .ConfigureAwait(false);
                }

                var repaired = outcomes.Where(outcome => outcome.Repaired).ToList();
                var outstanding = ledger.RecordDamage(
                    set.Id, destinationName, unrepaired: [], [.. repaired.Select(outcome => outcome.Key)], nowMs)
                    .DamagedKeys;

                ledger.RecordFailure(
                    set.Id, destinationName, DestinationSyncState.Failed,
                    $"deep verification found {result.Findings.Count} damaged object(s): {result.Findings[0]}; "
                    + (outstanding is { Count: > 0 }
                        ? DestinationSyncStore.DamageStatement(outstanding)
                        : "each was replaced from a sound copy and re-verified, and the next sync re-checks the destination"),
                    nowMs);
                runtime.Notices.Raise(
                    $"deep-verify-failed:{set.Id}:{destinationName}",
                    Finding(set, destinationName, result.Findings, outcomes),
                    nowMs);
                return (result.Examined, result.Findings.Count, repaired.Count, result.NextCursor, result.CompletedCircuit);
            }

            // A clean circuit no longer withdraws an earlier finding (ADR-0035
            // Amendment 1). Its objects may be sound now — a repair makes them
            // so at once — but the device altered a backup once, which is a
            // person's to hear about, and a notice withdrawn by the next clean
            // circuit could be withdrawn before anyone had read it.
            ledger.RecordSweep(
                set.Id, destinationName, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);
            return (result.Examined, 0, 0, result.NextCursor, result.CompletedCircuit);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The disk went away mid-segment. The cursor is not advanced, so
            // the next pass re-reads the same run; fan-out reports the
            // destination's availability.
            return (0, 0, 0, null, false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The notice a finding raises: what no longer matched, what replaced it,
    /// and what nothing could — then the device, which is the person's to
    /// look at whatever the service managed to tidy up.
    /// </summary>
    private static string Finding(
        BackupSetConfiguration set, string destinationName, IReadOnlyList<string> findings,
        IReadOnlyList<ReplicaRepairOutcome> outcomes)
    {
        var repaired = outcomes.Where(outcome => outcome.Repaired).ToList();
        var unrepaired = outcomes.Where(outcome => !outcome.Repaired).ToList();
        var sources = string.Join(", ", repaired.Select(outcome => outcome.RepairedFrom).Distinct(StringComparer.Ordinal));

        var said = $"destination '{destinationName}' of set '{set.Name}' was found holding {findings.Count} object(s) "
            + $"that no longer match what was sealed: {string.Join("; ", findings.Take(3))}. ";
        said += unrepaired.Count == 0
            ? $"Each was replaced from a sound copy ({sources}) and re-verified where it landed, so the destination "
                + "is whole again. "
            : (repaired.Count > 0
                ? $"{repaired.Count} were replaced from a sound copy ({sources}) and re-verified; "
                : string.Empty)
                + $"{unrepaired.Count} could not be, because no sound copy of them could be found "
                + $"({unrepaired[0].Detail}). Those bytes cannot be restored from there until they are replaced. ";
        return said + "Its storage altered a backup once and may again: check the device, the filesystem, and "
            + "anything else that writes there before counting on it.";
    }
}
