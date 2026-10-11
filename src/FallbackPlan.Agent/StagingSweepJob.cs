using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// Sweeps a staging set's own staging archive (FR-VER-009, ADR-0095): its
/// stored blobs re-read in full, a bounded segment at a time, each proven
/// against the digest sealed into its footer and bound to its key; and damage
/// found there replaced from a destination's copy proven sound — destination
/// repair in reverse.
/// </summary>
/// <remarks>
/// <para>
/// On the transfer lane under the set's gate, for the destination sweep's
/// reasons: a sync reads staging to fill a destination and a retention apply
/// deletes from it, so a segment beside either would read a blob mid-change.
/// A backup takes no gate and needs none here. It only adds blobs, and a key a
/// repair has emptied for a moment is, to its probe, one it may not reuse, so
/// it writes that content again.
/// </para>
/// <para>
/// Weekly, as a local path's default, and not configurable: the staging
/// archive is a disk this service owns, so reading it costs no one else
/// anything, and nothing there is paced. A segment is bounded as a local
/// path's is, because it holds the one transfer worker while it reads.
/// </para>
/// <para>
/// Damage is put on the set's record before anything is done about it, so a
/// repair cut short leaves it for the next try, and a try that leaves some of
/// it standing waits the sync's back-off before the next. Each finding is said
/// once, by what found it: the circuit, a destination's sweep, or a restore
/// that read around it.
/// </para>
/// </remarks>
internal static class StagingSweepJob
{
    /// <summary>Days between one finished circuit and the start of the next.</summary>
    public const int IntervalDays = ReplicaSweepJob.DefaultIntervalDays;

    private static readonly SegmentOutcome Nothing = new(0, 0, 0, CompletedCircuit: false);

    private static readonly AsyncLocal<Func<IObjectStore, IObjectStore>?> StagingDecoratorInFlow = new();

    /// <summary>
    /// Wraps the staging store each segment reads and repairs. A test hook
    /// scoped to the flow that sets it, as
    /// <see cref="ReplicaSweepJob.ReplicaDecorator"/> is: no real disk can be
    /// made to refuse one read on demand. Null, the production value, wraps
    /// nothing.
    /// </summary>
    internal static Func<IObjectStore, IObjectStore>? StagingDecorator
    {
        get => StagingDecoratorInFlow.Value;
        set => StagingDecoratorInFlow.Value = value;
    }

    /// <summary>What one segment read and found, and where it stopped short.</summary>
    /// <param name="Examined">Blobs read.</param>
    /// <param name="Damaged">Blobs this segment found not to match what was sealed.</param>
    /// <param name="Repaired">Of those, how many were replaced from a sound copy.</param>
    /// <param name="CompletedCircuit">Whether this segment reached the end of the archive.</param>
    internal sealed record SegmentOutcome(int Examined, int Damaged, int Repaired, bool CompletedCircuit)
    {
        /// <summary>The blob the segment stopped at because it would not read; null when none did.</summary>
        public string? StalledOn { get; init; }

        /// <summary>Why the segment stopped short; null when it did not.</summary>
        public string? Stall { get; init; }
    }

    /// <summary>
    /// The job identity: one per set, distinct from every destination's sweep
    /// whatever the destination is called.
    /// </summary>
    public static string JobIdFor(string setId) => $"staging-sweep-{setId}";

    /// <summary>The notice that says what was found in the set's staging archive and what became of it.</summary>
    public static string NoticeKey(string setId) => $"staging-damaged:{setId}";

    /// <summary>The notice that says the sweep keeps stopping at a blob that will not read.</summary>
    public static string StallNoticeKey(string setId) => $"staging-sweep-stalled:{setId}";

    /// <summary>
    /// Whether the set's staging sweep has anything due: a segment of its
    /// circuit, or damage waiting to be tried again.
    /// </summary>
    public static bool IsDue(ServiceRuntime runtime, BackupSetConfiguration set, ulong nowMs)
    {
        if (set.DirectShip)
        {
            return false;
        }

        var record = runtime.StagingSweeps.Find(set.Id);
        return CircuitDue(runtime, record, nowMs) || DamageDue(runtime, record, nowMs);
    }

    /// <summary>Queues the set's next segment; null when one is already queued or running.</summary>
    public static Task? Enqueue(ServiceRuntime runtime, BackupSetConfiguration set, DateTimeOffset now, bool userInitiated)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var queued = runtime.Queue.Enqueue(new QueuedJob(
            JobIdFor(set.Id),
            JobLane.Transfer,
            userInitiated,
            $"verify {set.Name} staging",
            async token => await RunAsync(runtime, set, nowMs, userInitiated, readCircuit: null, token)
                .ConfigureAwait(false),
            OnSettled: () => completion.TrySetResult()));

        return queued ? completion.Task : null;
    }

    /// <summary>
    /// Reads the circuit's next segment whatever its cadence, tries the damage
    /// outstanding when any is due, and says what it found.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose staging archive to sweep.</param>
    /// <param name="nowMs">The clock, Unix milliseconds.</param>
    /// <param name="userInitiated">Whether a person is waiting; a background repair reads its sources through their limits.</param>
    /// <param name="cancellationToken">Cancels the segment.</param>
    public static Task<SegmentOutcome> RunAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ulong nowMs, bool userInitiated,
        CancellationToken cancellationToken) =>
        RunAsync(runtime, set, nowMs, userInitiated, readCircuit: true, cancellationToken);

    /// <summary>
    /// Puts damage found in the staging archive by something other than its
    /// own circuit on the set's record, tries everything due, and says what
    /// became of what was not already known.
    /// </summary>
    /// <remarks>
    /// The caller holds the set's gate, as a destination's sweep does when its
    /// length comparison finds staging's copy unsound.
    /// </remarks>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The staging set.</param>
    /// <param name="archive">The set's archive.</param>
    /// <param name="found">Each key found damaged, with the line that says why.</param>
    /// <param name="foundBy">The destination whose sweep found it.</param>
    /// <param name="nowMs">The clock, Unix milliseconds.</param>
    /// <param name="userInitiated">Whether a person is waiting.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    internal static Task<IReadOnlyList<ReplicaRepairOutcome>> HealFoundAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive,
        IReadOnlyDictionary<string, string> found, string foundBy, ulong nowMs, bool userInitiated,
        CancellationToken cancellationToken) =>
        HealAsync(runtime, set, archive, archive.Store, found, readSound: [], foundBy, nowMs, userInitiated, cancellationToken);

    /// <summary>
    /// Whether the circuit's next segment is due: at once while a circuit is
    /// open, the back-off after a segment that stalled, and a week after the
    /// last circuit closed.
    /// </summary>
    private static bool CircuitDue(ServiceRuntime runtime, StagingSweepRecord? record, ulong nowMs)
    {
        if (record is null)
        {
            return true;
        }

        if (record.SweepStalls > 0 && record.SweepStalledAt is { } stalledAt)
        {
            return nowMs >= stalledAt + Scheduler.BackoffMs(runtime, record.SweepStalls);
        }

        if (record.SweptAt is not { } swept || record.SweepCursor is not null)
        {
            return true;
        }

        return nowMs >= swept + (ulong)IntervalDays * 24UL * 3_600_000UL;
    }

    /// <summary>
    /// Whether outstanding damage is due a try: at once when some of it has
    /// not been tried, and otherwise the sync's back-off after the last try.
    /// </summary>
    private static bool DamageDue(ServiceRuntime runtime, StagingSweepRecord? record, ulong nowMs) =>
        record?.DamagedKeys is { Count: > 0 }
        && (record.DamageCheckedAt is not { } checkedAt
            || nowMs >= checkedAt + Scheduler.BackoffMs(runtime, record.DamageChecks));

    private static async Task<SegmentOutcome> RunAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ulong nowMs, bool userInitiated, bool? readCircuit,
        CancellationToken cancellationToken)
    {
        if (set.DirectShip)
        {
            return Nothing;
        }

        // A direct-ship set's store reads its destinations, and the staging
        // archive a migrating set still holds is read-only and retiring.
        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is not { ShipSink: null })
        {
            return Nothing;
        }

        var staging = StagingDecorator is { } decorate ? decorate(archive.Store) : archive.Store;
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Asked again under the gate: a segment queued by one pass can run
            // after another's has already read what this one would.
            var circuit = readCircuit ?? CircuitDue(runtime, runtime.StagingSweeps.Find(set.Id), nowMs);
            return await SegmentAsync(runtime, set, archive, staging, circuit, nowMs, userInitiated, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<SegmentOutcome> SegmentAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive, IObjectStore staging,
        bool readCircuit, ulong nowMs, bool userInitiated, CancellationToken cancellationToken)
    {
        var store = runtime.StagingSweeps;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> readSound = [];
        ReplicaSweepResult? result = null;

        if (readCircuit)
        {
            var previous = store.Find(set.Id);

            // In-memory progress only, as a destination's sweep reports it:
            // the journal's last-completed is the backup's anchor.
            runtime.Progress.Report(new JobProgress(JobIdFor(set.Id), JobState.Verifying, 0, 0, 0, 0, 0, 0));

            try
            {
                result = await ReplicaSweep.RunAsync(
                    archive.Repository.RepositoryId,
                    archive.Repository.Keys,
                    staging,
                    source: null,
                    previous?.SweepCursor,
                    ReplicaSweepJob.SegmentBudget,
                    ReplicaSweep.DefaultByteBudget,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The archive could not be listed, so nothing was read.
                Stalled(runtime, set, store.RecordSweepStall(set.Id, cursor: null, examined: 0, stalledOn: null, nowMs),
                    exception.Message, nowMs);
                return Nothing with { Stall = exception.Message };
            }

            if (result.Stall is { } stall)
            {
                Stalled(runtime, set,
                    store.RecordSweepStall(set.Id, result.NextCursor, result.Examined, result.StalledOn, nowMs),
                    stall, nowMs);
            }
            else
            {
                store.RecordSweep(set.Id, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);
                runtime.Notices.Resolve(StallNoticeKey(set.Id), nowMs);
            }

            for (var index = 0; index < result.DamagedKeys.Count; index++)
            {
                found[result.DamagedKeys[index]] = result.Findings[index];
            }

            // Keys found before that this segment has now read, and read
            // sound. Only keys it read: one it did not reach is not thereby
            // shown to be whole.
            readSound = [.. (previous?.DamagedKeys ?? [])
                .Where(key => result.ExaminedKeys.Contains(key) && !found.ContainsKey(key))];
        }

        var outcomes = await HealAsync(
            runtime, set, archive, staging, found, readSound, foundBy: null, nowMs, userInitiated, cancellationToken)
            .ConfigureAwait(false);
        return new SegmentOutcome(
            result?.Examined ?? 0,
            found.Count,
            outcomes.Count(outcome => outcome.Repaired && found.ContainsKey(outcome.Key)),
            result?.CompletedCircuit ?? false)
        {
            StalledOn = result?.StalledOn,
            Stall = result?.Stall,
        };
    }

    private static async Task<IReadOnlyList<ReplicaRepairOutcome>> HealAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive, IObjectStore staging,
        IReadOnlyDictionary<string, string> found, List<string> readSound, string? foundBy,
        ulong nowMs, bool userInitiated, CancellationToken cancellationToken)
    {
        var store = runtime.StagingSweeps;
        var known = new HashSet<string>(store.Find(set.Id)?.DamagedKeys ?? [], StringComparer.Ordinal);

        // On the record before anything is done about it, so a repair cut
        // short leaves it for the next try. A key not already outstanding
        // makes all of it due at once.
        if (found.Count > 0 || readSound.Count > 0)
        {
            store.RecordDamage(set.Id, [.. found.Keys], readSound);
        }

        var outcomes = await TryOutstandingAsync(runtime, set, archive, staging, found, nowMs, userInitiated, cancellationToken)
            .ConfigureAwait(false);

        // What was already outstanding was said when it was found.
        List<KeyValuePair<string, string>> fresh = [.. found.Where(entry => !known.Contains(entry.Key))];
        if (fresh.Count > 0)
        {
            RaiseFound(runtime, set, archive, fresh, outcomes, foundBy, nowMs);
        }

        return outcomes;
    }

    /// <summary>
    /// Tries the outstanding damage when it is due: each key proven where it
    /// sits, unless <paramref name="found"/> already says why it is not sound,
    /// and each that is not, replaced from a destination's copy proven sound.
    /// </summary>
    /// <returns>What became of each key that was not sound; empty when nothing was due.</returns>
    private static async Task<IReadOnlyList<ReplicaRepairOutcome>> TryOutstandingAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive, IObjectStore staging,
        IReadOnlyDictionary<string, string> found, ulong nowMs, bool userInitiated, CancellationToken cancellationToken)
    {
        var store = runtime.StagingSweeps;
        var record = store.Find(set.Id);
        if (!DamageDue(runtime, record, nowMs))
        {
            return [];
        }

        var repositoryId = archive.Repository.RepositoryId;
        var keys = archive.Repository.Keys;
        var owed = new HashSet<string>(record!.RepairingKeys ?? [], StringComparer.Ordinal);
        var resolved = new List<string>();
        var outcomes = new List<ReplicaRepairOutcome>();
        try
        {
            await using var repairer = ReplicaRepairer.ForStaging(runtime, set, archive, userInitiated);
            foreach (var key in record.DamagedKeys!)
            {
                try
                {
                    if (!found.ContainsKey(key))
                    {
                        var held = await ReplicaRepair
                            .ProveAsync(repositoryId, keys, staging, ObjectKey.Parse(key), cancellationToken)
                            .ConfigureAwait(false);

                        // Sound now, or let go by the trim or the collector:
                        // either way it is not outstanding. Only a key a
                        // repair emptied is owed back.
                        if (held.Sound || (!held.Held && !owed.Contains(key)))
                        {
                            resolved.Add(key);
                            continue;
                        }
                    }

                    store.RecordRepairing(set.Id, key);
                    var outcome = (await repairer.RepairAsync(staging, [key], cancellationToken).ConfigureAwait(false))[0];
                    store.RecordRepairReturned(set.Id, key);
                    outcomes.Add(outcome);
                    if (outcome.Repaired)
                    {
                        resolved.Add(key);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The staging archive would not be read or written here.
                    // The key stays outstanding, and stays recorded as under
                    // repair in case the failure came between its delete and
                    // its put; the repairs after it are recorded beside it.
                    outcomes.Add(new ReplicaRepairOutcome(key, RepairedFrom: null, $"the staging archive refused it: {exception.Message}"));
                }
            }
        }
        finally
        {
            store.RecordDamage(set.Id, unrepaired: [], resolved);
            store.RecordDamageChecked(set.Id, nowMs);
        }

        return outcomes;
    }

    /// <summary>
    /// Says what was found in the staging archive, what replaced it, and what
    /// nothing could — then the disk, which is the person's to look at
    /// whatever the service managed to tidy up.
    /// </summary>
    private static void RaiseFound(
        ServiceRuntime runtime, BackupSetConfiguration set, ArchiveHandle archive,
        List<KeyValuePair<string, string>> fresh, IReadOnlyList<ReplicaRepairOutcome> outcomes,
        string? foundBy, ulong nowMs)
    {
        var keys = fresh.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var tried = outcomes.Where(outcome => keys.Contains(outcome.Key)).ToList();
        var repaired = tried.Where(outcome => outcome.Repaired).ToList();
        List<string> standing = [.. keys.Where(key => !repaired.Any(outcome => outcome.Key == key)).Order(StringComparer.Ordinal)];
        var detail = tried.FirstOrDefault(outcome => !outcome.Repaired)?.Detail ?? "it was not tried";
        var sources = string.Join(", ", repaired.Select(outcome => outcome.RepairedFrom).Distinct(StringComparer.Ordinal));
        var reach = standing.Count > 0 ? archive.TraceDamage(standing) : null;

        var said = $"The staging archive of set '{set.Name}' was found holding {fresh.Count} object(s) that no longer "
            + "match what was sealed"
            + (foundBy is null ? string.Empty : $", when destination '{foundBy}' was swept")
            + $": {ReplicaSweepJob.Listed(fresh.Select(entry => entry.Value))}. ";
        said += standing.Count == 0
            ? $"Each was replaced from a sound copy ({sources}) and re-verified where it landed, so the staging "
                + "archive is whole again. "
            : (repaired.Count > 0
                ? $"{repaired.Count} were replaced from a sound copy ({sources}) and re-verified; "
                : string.Empty)
                + $"{standing.Count} could not be ({detail}). Those bytes cannot be restored from the staging archive "
                + "until they are replaced, and the sweep tries again. "
                + (reach is null ? string.Empty : DamageReachText.Sentences(reach) + " ");
        runtime.Notices.Raise(
            NoticeKey(set.Id),
            said + "Its disk altered a backup once and may again: check the disk that holds it, and the filesystem, "
            + "before counting on it.",
            nowMs,
            reach is null ? null : DamageReachText.Names(set.Id, reach));
    }

    /// <summary>
    /// Says a stall once it has happened as many times in a row as a
    /// destination's must: a condition, not a finding, so the next segment
    /// that reads past it withdraws it.
    /// </summary>
    private static void Stalled(
        ServiceRuntime runtime, BackupSetConfiguration set, StagingSweepRecord record, string reason, ulong nowMs)
    {
        if (record.SweepStalls < ReplicaSweepJob.StallsBeforeNotice)
        {
            return;
        }

        var where = record.SweepStalledOn is { } key
            ? $"has stopped {record.SweepStalls} times in a row at blob {key}, which would not read: {reason}. "
                + "Until it reads, what needs that blob cannot be restored from the staging archive."
            : $"could not read the staging archive {record.SweepStalls} times in a row: {reason}. "
                + "Nothing there is being re-read until it can.";
        runtime.Notices.Raise(
            StallNoticeKey(set.Id),
            $"The sweep of the staging archive of set '{set.Name}' {where} Nothing is known to be altered, and the "
            + "sweep keeps trying, further apart each time; a disk that will not give back a backup's bytes is one "
            + "to check.",
            nowMs);
    }
}
