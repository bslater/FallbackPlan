using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

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

    /// <summary>
    /// Stalls in a row before the sweep says so. Two could be a drive
    /// re-seated mid-read; three, minutes apart under the back-off, is a
    /// device that will not give up a backup's bytes.
    /// </summary>
    internal const int StallsBeforeNotice = 3;

    /// <summary>
    /// Wraps every replica a segment opens. A test hook scoped to the flow
    /// that sets it, as <see cref="SegmentBudget"/> is: no real disk can be
    /// made to refuse one read on demand, and what the sweep does then is
    /// what a test of it needs. Null, the production value, wraps nothing.
    /// </summary>
    internal static Func<IObjectStore, IObjectStore>? ReplicaDecorator
    {
        get => ReplicaDecoratorInFlow.Value;
        set => ReplicaDecoratorInFlow.Value = value;
    }

    private static readonly AsyncLocal<Func<IObjectStore, IObjectStore>?>
        ReplicaDecoratorInFlow = new();

    /// <summary>What one segment did, or a full pass did in all.</summary>
    /// <param name="Examined">Blobs read.</param>
    /// <param name="Damaged">Blobs found not to match what was sealed.</param>
    /// <param name="Repaired">Of those, how many were replaced from a sound copy.</param>
    /// <param name="Cursor">Where the next segment resumes; null when the circuit closed.</param>
    /// <param name="CompletedCircuit">Whether this segment reached the end of the replica.</param>
    internal sealed record SegmentOutcome(int Examined, int Damaged, int Repaired, string? Cursor, bool CompletedCircuit)
    {
        /// <summary>The blob the segment stopped at because it would not read; null when none did.</summary>
        public string? StalledOn { get; init; }

        /// <summary>Why the segment stopped short; null when it did not.</summary>
        public string? Stall { get; init; }
    }

    private static readonly SegmentOutcome Nothing = new(0, 0, 0, null, false);

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
    /// about what counts as damage. It stops at a stall, which the next
    /// segment would only meet again.
    /// </remarks>
    public static async Task<SegmentOutcome> RunFullAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        var total = Nothing;

        // Bounded by the number of segments a circuit can take, not by trust
        // that one will close: a cursor that somehow failed to advance must
        // end the loop rather than spin.
        string? previousCursor = null;
        for (var segment = 0; segment < 1_000_000; segment++)
        {
            // The on-demand full pass is a person's (FR-VER-004), so it reads
            // through no limit.
            var outcome = await SweepAsync(runtime, set, destinationName, nowMs, userInitiated: true, cancellationToken)
                .ConfigureAwait(false);
            total = outcome with
            {
                Examined = total.Examined + outcome.Examined,
                Damaged = total.Damaged + outcome.Damaged,
                Repaired = total.Repaired + outcome.Repaired,
            };

            if (outcome.Stall is not null
                || outcome.CompletedCircuit || outcome.Cursor is null || outcome.Cursor == previousCursor)
            {
                break;
            }

            previousCursor = outcome.Cursor;
        }

        return total;
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
        var outcome = await SweepAsync(runtime, set, destinationName, nowMs, userInitiated, cancellationToken)
            .ConfigureAwait(false);
        return (outcome.Examined, outcome.Damaged, outcome.Repaired, outcome.Cursor, outcome.CompletedCircuit);
    }

    /// <summary>Reads the next segment, records what it found, and says where it stopped short.</summary>
    /// <inheritdoc cref="RunAsync" path="/param"/>
    public static async Task<SegmentOutcome> SweepAsync(
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
            return Nothing;
        }

        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return Nothing;
        }

        var replicaRoot = Path.Combine(destination.Path!, archive.Repository.RepositoryId.ToString());
        if (!Directory.Exists(replicaRoot))
        {
            // Unreachable right now. Fan-out owns saying so — its shortfall and
            // availability signals already cover a replica that has gone, and
            // a second voice saying it would be a second notice to acknowledge.
            // Nor is it a stall: nothing was tried, and a drive plugged back
            // in resumes where it was.
            return Nothing;
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
            if (ReplicaDecorator is { } decorate)
            {
                replica = decorate(replica);
            }

            ReplicaSweepResult result;
            try
            {
                result = await ReplicaSweep.RunAsync(
                    archive.Repository.RepositoryId,
                    archive.Repository.Keys,
                    replica,
                    archive.Store,
                    previous?.SweepCursor,
                    SegmentBudget,
                    limiter is null ? ReplicaSweep.DefaultByteBudget : Math.Max(1, limiter.Rate.BytesPerSecond * SegmentSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The replica could not be listed, so nothing was read: a
                // stall with no blob to name, waited out as any other is.
                Stalled(runtime, set, destinationName,
                    ledger.RecordSweepStall(set.Id, destinationName, cursor: null, examined: 0, stalledOn: null, nowMs),
                    exception.Message, nowMs);
                return Nothing with { Stall = exception.Message };
            }

            // What the segment read stands, whether it finished or stopped at
            // a blob it could not read; a stall is counted, and the scheduler
            // waits it out under the back-off instead of meeting it again on
            // the next pass.
            if (result.Stall is { } stall)
            {
                Stalled(runtime, set, destinationName,
                    ledger.RecordSweepStall(
                        set.Id, destinationName, result.NextCursor, result.Examined, result.StalledOn, nowMs),
                    stall, nowMs);
            }
            else
            {
                ledger.RecordSweep(
                    set.Id, destinationName, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);
                runtime.Notices.Resolve($"deep-verify-stalled:{set.Id}:{destinationName}", nowMs);
            }

            if (result.Findings.Count > 0)
            {
                // FR-VER-005: a verification failure degrades the pair and
                // raises a warning requiring action. The cursor still advances
                // — a damaged blob must not park the sweep on itself forever,
                // re-reporting the same object while the rest goes unchecked.
                // The keys go on the ledger before anything is done about
                // them, so a repair cut short leaves them to the next sync
                // rather than forgotten (FR-VER-007).
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
                return new SegmentOutcome(
                    result.Examined, result.Findings.Count, repaired.Count, result.NextCursor, result.CompletedCircuit)
                {
                    StalledOn = result.StalledOn,
                    Stall = result.Stall,
                };
            }

            // A clean circuit no longer withdraws an earlier finding (ADR-0035
            // Amendment 1). Its objects may be sound now — a repair makes them
            // so at once — but the device altered a backup once, which is a
            // person's to hear about, and a notice withdrawn by the next clean
            // circuit could be withdrawn before anyone had read it.
            return new SegmentOutcome(result.Examined, 0, 0, result.NextCursor, result.CompletedCircuit)
            {
                StalledOn = result.StalledOn,
                Stall = result.Stall,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The disk went away after the segment read, while what it found
            // was being repaired: the keys are on the ledger already, and the
            // sync re-checks them.
            return Nothing;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Says a stall once it has happened <see cref="StallsBeforeNotice"/>
    /// times in a row: a condition, not a finding, since nothing has been
    /// shown altered — so it is withdrawn by the next segment that finishes.
    /// </summary>
    private static void Stalled(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DestinationSyncRecord row,
        string reason, ulong nowMs)
    {
        if (row.SweepStalls < StallsBeforeNotice)
        {
            return;
        }

        var where = row.SweepStalledOn is { } key
            ? $"has stopped {row.SweepStalls} times in a row at blob {key}, which would not read: {reason}. "
                + "Until it reads, what needs that blob cannot be restored from there."
            : $"could not read the replica {row.SweepStalls} times in a row: {reason}. "
                + "Nothing there is being re-read until it can.";
        runtime.Notices.Raise(
            $"deep-verify-stalled:{set.Id}:{destinationName}",
            $"The deep sweep of destination '{destinationName}' of set '{set.Name}' {where} Nothing is known to be "
            + "altered, and the sweep keeps trying, further apart each time; a device that will not give back a "
            + "backup's bytes is one to check.",
            nowMs);
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
