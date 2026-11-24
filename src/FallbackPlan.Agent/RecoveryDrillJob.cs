using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// The scheduled restore drill (ADR-0054; FR-DRL-002, NFR-OPS-005): bring a
/// sampled file back out of a destination's own replica, on a cadence, and
/// record what happened so "we could recover" stops being a claim about the
/// last time somebody chose to check.
/// </summary>
/// <remarks>
/// <para>
/// A drill is not a verification, and the two are kept apart because they
/// fail apart. A possession challenge proves a destination still holds
/// authentic bytes; a drill proves the road from those bytes back to a file
/// is open — the index plane rebuilds, the catalogue projects, the manifest
/// decodes, the segments assemble in order, and the reassembly hashes to what
/// the capture recorded. A replica can pass every challenge and fail every
/// one of those.
/// </para>
/// <para>
/// It runs through the ordinary guided-restore verbs rather than a private
/// copy of them, which is the point: what the drill exercises is what a
/// person would use, so the two cannot drift apart. Those verbs open the
/// replica the way a stranger would — its own store, its own repository open,
/// a throwaway catalogue rebuilt from its own index plane (ADR-0041) — so
/// nothing here can be answered by the live archive.
/// </para>
/// <para>
/// It is therefore <b>not</b> queued on a lane. The verbs it calls queue
/// themselves on the reader lane, which has one worker, so a drill holding
/// that worker while waiting for it would wait for ever.
/// </para>
/// </remarks>
internal static class RecoveryDrillJob
{
    /// <summary>Days between drills when a destination states no preference.</summary>
    public const int DefaultIntervalDays = 30;

    /// <summary>Files one drill restores.</summary>
    /// <remarks>
    /// Enough that a single unlucky pick cannot make a broken replica look
    /// sound, few enough that the drill stays a background cost. The bytes
    /// are bounded by the sample, not by the archive: the expensive half is
    /// the catalogue rebuild, which is paid once per drill however many files
    /// follow it.
    /// </remarks>
    public const int SampleFiles = 3;

    /// <summary>How deep the random descent will go looking for a file.</summary>
    private const int MaximumDepth = 24;

    /// <summary>The most one sampled file may weigh when the replica is a peer's.</summary>
    /// <remarks>
    /// A local path is this machine's own disk and is not capped. A peer's
    /// replica is read over somebody else's link, and a drill that happened
    /// to sample a disk image would spend hours of it on a schedule the peer
    /// never saw. A file over the cap is not chosen and the descent tries
    /// again; the drill says how many it left ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)
    /// Amendment 3).
    /// </remarks>
    public const long PeerSampleFileCap = 64L * 1024 * 1024;

    /// <summary>The most one drill may pull from a peer in total.</summary>
    /// <remarks>
    /// Below <see cref="SampleFiles"/> times the per-file cap on purpose: a
    /// total that could never bite would be a number that says nothing. Two
    /// files at the per-file cap fit; a third does not, and is left.
    /// </remarks>
    public const long PeerSampleTotalCap = 128L * 1024 * 1024;

    /// <summary>What one drill may read: the largest file, and the most in total.</summary>
    /// <param name="FileCap">A file longer than this is not sampled.</param>
    /// <param name="TotalCap">Once the chosen files reach this, no more are chosen.</param>
    public sealed record SampleBudget(long FileCap, long TotalCap)
    {
        /// <summary>The budget a peer's replica is drilled under.</summary>
        public static SampleBudget Peer { get; } = new(PeerSampleFileCap, PeerSampleTotalCap);
    }

    /// <summary>What one drill found.</summary>
    /// <param name="Files">
    /// Files restored whole — or, under <paramref name="Limit"/>, proved as far
    /// as the sealed content, or as far as content held only in an archival tier.
    /// </param>
    /// <param name="Bytes">What the files restored whole amounted to; nothing proved only part of the way counts.</param>
    /// <param name="Failure">Why it did not work, or null when it did.</param>
    /// <param name="Limit">What a passing drill could not prove, or null when it proved everything.</param>
    public sealed record DrillOutcome(int Files, long Bytes, string? Failure, string? Limit = null)
    {
        /// <summary>
        /// Sampled files whose content the store holds only in an archival
        /// tier, proved as far as that content and not read
        /// ([ADR-0105](../../docs/adr/0105-a-drill-takes-archived-content-as-a-stated-limit.md)).
        /// </summary>
        public int Archived { get; init; }

        /// <summary>
        /// Whether the drill stated anything. False for a drill the service
        /// stopping cut short, which records nothing and raises nothing
        /// (ADR-0054 Amendments 1 and 4), so whoever asked for it is answered
        /// cancelled rather than told it passed.
        /// </summary>
        public bool Recorded { get; init; } = true;

        /// <summary>
        /// The file, or the folder, the drill was at when it failed, as the
        /// snapshot names it; null when it failed at none. Never part of
        /// <see cref="Failure"/>, which goes where anyone signed in reads it:
        /// it is kept beside the drill's notice for whoever unlocks the set
        /// with its passphrase (FR-WOR-007, ADR-0089 Amendment 1).
        /// </summary>
        public string? FailedPath { get; init; }
    }

    /// <summary>
    /// The limit a drill states on a write-only set (ADR-0054 Amendment 2):
    /// the service holds no content key, so the road back is proved as far
    /// as the sealed content and no further.
    /// </summary>
    public const string SealedContentLimit =
        "content sealed: this set is write-only, so the service could prove the road back only as far as the "
        + "sealed content — the replica opens, its index and catalogue rebuild, and every sampled file's manifest "
        + "and segment records were found — and could not read the content itself without the passphrase. A "
        + "content drill is the recovery tool with the passphrase (ADR-0054).";

    /// <summary>
    /// Drills one (set, destination) pair and records the result, under the
    /// budget the destination's kind implies: none for a local path, the
    /// peer budget for a peer and for an object store, whose every byte read
    /// back crosses a network and, at a provider, a bill.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    public static Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        var budget = runtime.Configuration.FindDestination(destinationName) is
            { Kind: DestinationKind.Peer or DestinationKind.S3 or DestinationKind.AzureBlob }
            ? SampleBudget.Peer
            : null;
        return RunAsync(runtime, set, destinationName, nowMs, budget, cancellationToken);
    }

    /// <summary>
    /// Drills one pair because a person asked (FR-DRL-003, ADR-0054
    /// Amendment 6), as the scheduler would, under the budget the
    /// destination's kind implies and through no transfer limit.
    /// </summary>
    /// <remarks>
    /// The drill runs on the service's lifetime, not the asker's: a person
    /// who stops waiting is answered cancelled, and the drill they started
    /// finishes and is recorded, as an on-demand sync does. Only the service
    /// stopping cuts it short, and then it states nothing.
    /// </remarks>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="cancellationToken">Ends the wait for the answer, never the drill.</param>
    public static Task<DrillOutcome> RunOnRequestAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken) =>
        RunAsync(runtime, set, destinationName, nowMs, userInitiated: true, runtime.Queue.StoppingToken)
            .WaitAsync(cancellationToken);

    /// <summary>
    /// Why a person's ask finds nothing at a pair to drill, beyond the set
    /// having no snapshot, which the asker settles first: null when there is
    /// something there to restore.
    /// </summary>
    /// <remarks>
    /// The scheduler's rule for a pair nothing has reached holds for a
    /// person's ask too: drilling would record a failure about an absence
    /// that is correct. Its cadence rule does not: a peer with no stated
    /// cadence is drilled when a person asks, because one drill somebody
    /// chose is not a standing cost on the peer's link.
    /// </remarks>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set asked about.</param>
    /// <param name="destinationName">The destination asked about.</param>
    internal static string? NothingToDrill(ServiceRuntime runtime, BackupSetConfiguration set, string destinationName)
    {
        if (runtime.Configuration.FindDestination(destinationName) is not { } destination)
        {
            return "the destination is no longer declared";
        }

        if (destination.Kind is not (DestinationKind.LocalPath or DestinationKind.Peer)
            && !destination.Kind.IsObjectStore())
        {
            return $"a {destination.Kind} destination is not served yet";
        }

        return runtime.DestinationSync.Find(set.Id, destinationName)?.LastSuccessAt is null
            ? "nothing has been copied there yet, so there is nothing there to restore"
            : null;
    }

    /// <summary>What a drill found, as the clause a person's report gives it.</summary>
    /// <param name="outcome">A drill's recorded outcome.</param>
    internal static string Describe(DrillOutcome outcome) => outcome switch
    {
        { Failure: { } failure } => $"could not restore — {failure}",
        { Limit: { } limit } => $"passed, {outcome.Files} file(s) proved — {limit}",
        { Files: > 0 } => $"restored {outcome.Files} file(s) from its replica",
        _ => "passed — its newest snapshot holds no file to restore",
    };

    /// <summary>
    /// Drills one pair as the scheduler does, under the budget the
    /// destination's kind implies. A background drill reads the replica
    /// through the destination's transfer limit (NFR-PERF-013, ADR-0074) — a
    /// peer's drill spends somebody else's link — and a person's reads
    /// through none.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="userInitiated">Whether a person is waiting for it.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    public static Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        bool userInitiated,
        CancellationToken cancellationToken)
    {
        var budget = runtime.Configuration.FindDestination(destinationName) is
            { Kind: DestinationKind.Peer or DestinationKind.S3 or DestinationKind.AzureBlob }
            ? SampleBudget.Peer
            : null;
        return RunAsync(
            runtime,
            HandlerFor(runtime, pacesSources: !userInitiated),
            set,
            destinationName,
            nowMs,
            budget,
            Random.Shared,
            cancellationToken);
    }

    /// <summary>
    /// Drills one (set, destination) pair and records the result. Never
    /// throws: a drill is a check, and a check that takes the scheduler down
    /// is worse than the condition it was looking for.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="budget">What the sample may read, or null for no cap.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    public static Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        SampleBudget? budget,
        CancellationToken cancellationToken) =>
        RunAsync(runtime, set, destinationName, nowMs, budget, Random.Shared, cancellationToken);

    /// <summary>
    /// Drills one (set, destination) pair with the draw the sample's descent
    /// makes — a test's way to hold the descent's choices still.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="budget">What the sample may read, or null for no cap.</param>
    /// <param name="draw">Chooses among the entries the descent is offered at each step.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    internal static Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        SampleBudget? budget,
        Random draw,
        CancellationToken cancellationToken) =>
        RunAsync(
            runtime, HandlerFor(runtime, pacesSources: false), set, destinationName,
            nowMs, budget, draw,
            cancellationToken);

    /// <summary>
    /// Drills one (set, destination) pair through the service it is handed —
    /// a test's way to put one fault between the drill and the verbs it
    /// calls, so that the fault is all that differs from a clean drill. A
    /// drill of the pair already under way is joined rather than started
    /// again, whoever started it (FR-DRL-003).
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="handler">The verbs the drill calls.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="budget">What the sample may read, or null for no cap.</param>
    /// <param name="draw">Chooses among the entries the descent is offered at each step.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    internal static Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        IFallbackPlanService handler,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        SampleBudget? budget,
        Random draw,
        CancellationToken cancellationToken) =>
        runtime.Drills.RunOrJoin(
            set.Id,
            destinationName,
            () => DrillAndRecordAsync(runtime, handler, set, destinationName, nowMs, budget, draw, cancellationToken));

    /// <summary>
    /// The handler a drill restores its samples through: the service's own,
    /// whose restores are never on record as a person's are (ADR-0115
    /// Amendment 1), pacing its reads where nobody waits on it.
    /// </summary>
    private static ServiceCommandHandler HandlerFor(ServiceRuntime runtime, bool pacesSources) =>
        new(runtime, RemoteBindingState.Off, CallerScope.Service)
        {
            PacesRestoreSources = pacesSources,
            RestoresForADrill = true,
        };

    private static async Task<DrillOutcome> DrillAndRecordAsync(
        ServiceRuntime runtime,
        IFallbackPlanService handler,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        SampleBudget? budget,
        Random draw,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(
            runtime.RestoreCacheRoot, $"drill-{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..16]}");
        string? sourceId = null;
        string? at = null;
        string? drilled = null;
        string? newest = null;

        // The backup proven where the drill reads (ADR-0199 Amendment 11),
        // as the ledger names it when the drill begins.
        var proven = runtime.DestinationSync.Find(set.Id, destinationName)?.ProvenSnapshot is { } provenKey
            ? NewBackupProof.IdentityOf(provenKey)
            : null;

        try
        {
            var outcome = await DrillAsync(
                handler, set, destinationName, scratch, budget, draw, id => sourceId = id, path => at = path,
                id => drilled = id, id => newest = id, proven,
                plansFirst: runtime.Configuration.FindDestination(destinationName)?.Kind.IsObjectStore() == true,
                cancellationToken)
                .ConfigureAwait(false);

            // A failure reached while the service is stopping is about the
            // service, not the replica: the store it read from, or the source
            // it opened, may be going away underneath it. The row keeps its
            // last answer, and the next start drills the pair again.
            if (outcome.Failure is not null && Stopping(runtime, cancellationToken))
            {
                return Unrecorded;
            }

            // A passing drill proves the files it sampled come back, not every
            // blob the backup needs whole at the destination: where the
            // backup's proof there had not ended, that is a limit of its own
            // (ADR-0199 Amendment 8). Where the drill restored the backup
            // proven there in place of the newest, which backup it restored,
            // and why, is the limit (Amendment 11). Only a passing drill
            // proves files; a drill that failed proved none.
            if (outcome is { Files: > 0 } && drilled is not null
                && (drilled != newest
                    ? await RestoredInsteadAsync(runtime, set, destinationName, newest!, drilled, cancellationToken).ConfigureAwait(false)
                    : await NotYetProvenAsync(runtime, set, destinationName, drilled, cancellationToken).ConfigureAwait(false))
                    is { } notYetProven)
            {
                outcome = outcome with { Limit = outcome.Limit is null ? notYetProven : $"{outcome.Limit} {notYetProven}" };
            }

            runtime.DestinationSync.RecordDrill(
                set.Id, destinationName, outcome.Files, outcome.Bytes, outcome.Failure, outcome.Limit, nowMs);
            Announce(runtime, set, destinationName, outcome, nowMs);

            // The drill is one of the replica's readers, so what it met held
            // only in an archival tier is said in the pair's one notice for it.
            if (outcome.Archived > 0)
            {
                ArchivedReplicaNotice.Reflect(
                    runtime, set, destinationName, runtime.DestinationSync.RecordArchived(set.Id, destinationName, nowMs),
                    nowMs);
            }

            return outcome;
        }
        catch (Exception) when (Stopping(runtime, cancellationToken))
        {
            // A drill cut short because the service is stopping states
            // nothing (ADR-0054 Amendment 1): it did not pass and it did not
            // find damage, so the row keeps whatever the last completed drill
            // said, and the next pass tries again. Whatever it met on the way
            // out, a cancelled command, a disposed object or anything else,
            // came from the service taking itself apart.
            return Unrecorded;
        }
        catch (Exception exception)
        {
            // Anything else that ends a drill while the service runs is a
            // drill that did not complete, and says so (Amendment 4). A
            // cancellation or a disposed object is not shutdown unless the
            // service is stopping; met while it runs, it is a fault on the
            // road back, and silence would hide it for as long as it lasted.
            var failure = $"the drill did not complete: {Unnamed(exception.Message.ReplaceLineEndings(" "), at)}";
            runtime.DestinationSync.RecordIncompleteDrill(set.Id, destinationName, failure, nowMs);
            var outcome = new DrillOutcome(0, 0, failure) { FailedPath = at };
            Announce(runtime, set, destinationName, outcome, nowMs);
            return outcome;
        }
        finally
        {
            if (sourceId is not null)
            {
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(sourceId), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            TryDelete(scratch);
        }
    }

    /// <summary>
    /// How long after its last drill a pair is due another
    /// ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)
    /// Amendments 4 and 5): sooner than its interval only when the last drill
    /// left something a new one could answer differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A drill that did not complete answered nothing about the replica, so
    /// it is retried on the back-off whatever has happened since.
    /// </para>
    /// <para>
    /// A drill that failed answered about the replica it found. Once a sync
    /// has succeeded since it, with no damage standing there, that replica may
    /// have been put right, so the failure is checked again on the same
    /// back-off. A sync in the drill's own pass ran before it, so the drill
    /// saw what it copied, and a sync around damage the deep sweep found and
    /// could not replace has put nothing right: either way the answer stands
    /// for the interval, as does a drill that passed.
    /// </para>
    /// </remarks>
    /// <param name="record">The pair's ledger row, with a drill on it.</param>
    /// <param name="intervalMs">The pair's drill interval.</param>
    /// <returns>How long after <see cref="DestinationSyncRecord.DrilledAt"/> the pair is due again.</returns>
    internal static ulong DrillWaitMs(DestinationSyncRecord record, ulong intervalMs)
    {
        if (record.ConsecutiveIncompleteDrills > 0)
        {
            return RetryMs(record.ConsecutiveIncompleteDrills, intervalMs);
        }

        var changedSince = record.LastSuccessAt > record.DrilledAt && record.DamagedKeys is not { Count: > 0 };
        return record.DrillFailure is not null && changedSince
            ? RetryMs(Math.Max(1, record.ConsecutiveFailedDrills), intervalMs)
            : intervalMs;
    }

    /// <summary>
    /// The back-off a drill retry waits: an hour after the first drill of the
    /// run, doubling with each one after it, and never later than the pair's
    /// own interval.
    /// </summary>
    /// <remarks>
    /// Sooner than the interval, because a fault that passed in a minute
    /// should not stand as a failed drill for a month on an installation
    /// nobody is watching. A person can drill at once (FR-DRL-003), and the
    /// schedule must not depend on one doing so. Not every pass, because a
    /// fault that lasts would then drill every minute, and a peer's drill
    /// reads over somebody else's link.
    /// </remarks>
    /// <param name="inARow">Drills in the run so far; one or more.</param>
    /// <param name="intervalMs">The pair's drill interval.</param>
    /// <returns>How long after the last drill the pair is due again.</returns>
    internal static ulong RetryMs(int inARow, ulong intervalMs)
    {
        var doublings = Math.Clamp(inARow - 1, 0, 30);
        return Math.Min(RetryFirstMs << doublings, intervalMs);
    }

    /// <summary>The first retry of a run: an hour.</summary>
    private const ulong RetryFirstMs = 3_600_000;

    /// <summary>What a drill that states nothing returns: no files, no failure, no limit, and nothing recorded.</summary>
    private static DrillOutcome Unrecorded { get; } = new(0, 0, null) { Recorded = false };

    /// <summary>
    /// Whether the service is stopping, which is the only thing that makes a
    /// drill's ending say nothing: its queue or its runtime has begun shutting
    /// down, or the pass that ran the drill, which runs on the service's
    /// lifetime, was cancelled.
    /// </summary>
    private static bool Stopping(ServiceRuntime runtime, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || runtime.IsStopping;

    /// <summary>
    /// Turns a command answered as cancelled back into the cancellation it
    /// was, naming the step it cut short.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceCommandHandler"/> answers every cancellation as a
    /// <see cref="ServiceErrorReason.Cancelled"/> error rather than throwing,
    /// which is right for a client and wrong for this caller. Read as an
    /// answer about the replica, a cancellation during shutdown wrote "your
    /// backups may not be restorable". Thrown instead, it ends the drill, and
    /// whether that ending says anything is decided in one place, by whether
    /// the service is stopping.
    /// </remarks>
    /// <param name="error">The refusal to inspect.</param>
    /// <param name="step">What the drill was doing, for the record of a drill that did not complete.</param>
    private static void ThrowIfCancelled(ServiceError error, string step)
    {
        if (error.Reason == ServiceErrorReason.Cancelled)
        {
            throw new OperationCanceledException($"{step} was cancelled");
        }
    }

    private static async Task<DrillOutcome> DrillAsync(
        IFallbackPlanService handler,
        BackupSetConfiguration set,
        string destinationName,
        string scratch,
        SampleBudget? budget,
        Random draw,
        Action<string> keepSourceId,
        Action<string> keepPath,
        Action<string> keepSnapshot,
        Action<string> keepNewest,
        string? proven,
        bool plansFirst,
        CancellationToken cancellationToken)
    {
        var opened = await handler.ExecuteAsync(
            new OpenRestoreSourceCommand(set.Name, destinationName), cancellationToken).ConfigureAwait(false);
        if (opened is ServiceError openFailure)
        {
            ThrowIfCancelled(openFailure, "opening the replica");
            return new DrillOutcome(0, 0, $"the replica would not open: {openFailure.Message}");
        }

        if (opened is not RestoreSourceOpenedResult source)
        {
            return new DrillOutcome(0, 0, $"opening the replica answered {opened.GetType().Name}.");
        }

        keepSourceId(source.SourceId);

        // The newest snapshot of this set. Newest rather than a random one
        // because it is the one a person would reach for, and because an
        // older snapshot's segments may legitimately have been trimmed from a
        // destination under its own retention (FR-GC-010) — a drill that
        // sampled those would report damage about a policy working.
        var ofSet = source.Snapshots
            .Where(snapshot => string.Equals(snapshot.BackupSetId, set.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var newestHeld = ofSet.OrderByDescending(snapshot => snapshot.CapturedAt).FirstOrDefault();
        if (newestHeld is null)
        {
            return new DrillOutcome(0, 0, "the replica holds no snapshot of this set.");
        }

        keepNewest(newestHeld.SnapshotId);

        // Unless the destination still holds the backup proven there: that
        // one's every blob is known whole there, so it is the backup the
        // drill's samples speak for while the newest's proof is under way
        // (ADR-0199 Amendment 11).
        var newest = ofSet.FirstOrDefault(snapshot =>
                string.Equals(snapshot.SnapshotId, proven, StringComparison.OrdinalIgnoreCase))
            ?? newestHeld;
        keepSnapshot(newest.SnapshotId);

        var (paths, skipped) = await SampleAsync(
            handler, source.SourceId, newest.SnapshotId, budget, draw, keepPath, cancellationToken).ConfigureAwait(false);
        if (paths.Count == 0)
        {
            // A snapshot of an empty tree is not a failure to restore from —
            // there is nothing in it to bring back, and calling that a broken
            // recovery would raise an alarm about a set that captured nothing.
            // Nor is a snapshot whose every reached file is over the budget:
            // the replica opened and listed over the wire, and what was not
            // read is stated as a limit rather than blamed on the peer.
            if (newest.Files == 0)
            {
                return new DrillOutcome(0, 0, null);
            }

            return skipped > 0
                ? new DrillOutcome(0, 0, null, CapLimit(skipped, budget!))
                : new DrillOutcome(0, 0, $"snapshot {newest.SnapshotId} lists {newest.Files} file(s), and none could be sampled.");
        }

        var files = 0;
        var bytes = 0L;
        var sealedFiles = 0;
        var archivedFiles = 0;
        foreach (var path in paths)
        {
            keepPath(path);

            // A store with an archival tier refuses a read of what it holds
            // only there, which is no damage (ADR-0105). A plan says so from
            // the store's answers to a HEAD of each object, so what it names
            // archived is not asked for.
            var planned = false;
            if (plansFirst)
            {
                var (plan, unplanned) = await PlanAsync(handler, newest.SnapshotId, path, source.SourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (unplanned is not null)
                {
                    return unplanned;
                }

                if (plan!.ArchivedObjects is { Count: > 0 })
                {
                    archivedFiles++;
                    continue;
                }

                planned = true;
            }

            var restored = await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest.SnapshotId, path, Path.Combine(scratch, $"{files}"), source.SourceId, InPlace: true),
                cancellationToken).ConfigureAwait(false);

            switch (restored)
            {
                case ServiceError error:
                    ThrowIfCancelled(error, "restoring a sampled file");
                    return Failed($"a sampled file would not restore: {Unnamed(error.Message, path)}", path);

                // The whole-file hash is checked inside the restore, after
                // reassembly and before a byte is emitted (specification 06
                // §4.2), so a complete restore is the proof — there is no
                // second comparison to make here and inventing one would only
                // be able to disagree with the engine.
                case RestoreResult { Outcome: "complete", Failed: 0 } ok:
                    files++;
                    bytes += ok.Restored > 0 ? BytesUnder(Path.Combine(scratch, $"{files - 1}")) : 0;
                    break;

                // A write-only set's content is sealed to a key the service
                // does not hold (ADR-0042 §7), so the engine reached every
                // segment record and stopped there. That is the road back
                // proved as far as it can be from inside the service, and a
                // stated limit rather than damage — provided the plan finds
                // every segment the manifest names, which a missing or
                // unreadable record would fail.
                case RestoreResult sealedOnly when SealedOnly(sealedOnly):
                    if (!planned
                        && (await PlanAsync(handler, newest.SnapshotId, path, source.SourceId, cancellationToken)
                            .ConfigureAwait(false)).Failure is { } unplanned)
                    {
                        return unplanned;
                    }

                    sealedFiles++;
                    break;

                case RestoreResult other:
                    return Failed(
                        $"a sampled file restored {other.Outcome} — {other.Failed} failed"
                        + (other.FailedSample is { Count: > 0 } sample ? $": {Unnamed(sample[0], path)}" : "."),
                        path);

                default:
                    return Failed($"restoring a sampled file answered {restored.GetType().Name}.", path);
            }
        }

        var limits = new List<string>(3);
        if (sealedFiles > 0)
        {
            limits.Add(SealedContentLimit);
        }

        if (archivedFiles > 0)
        {
            limits.Add(ArchivedLimit(archivedFiles));
        }

        if (skipped > 0)
        {
            limits.Add(CapLimit(skipped, budget!));
        }

        return new DrillOutcome(
            files + sealedFiles + archivedFiles, bytes, null, limits.Count == 0 ? null : string.Join(" ", limits))
        {
            Archived = archivedFiles,
        };
    }

    /// <summary>
    /// What a passing drill states where it restored the backup proven at the
    /// destination in place of the newest there (ADR-0199 Amendment 11): that
    /// the newest was not yet proven there, how far its check had got, and
    /// which backup the drill restored. Never null: the drill restored another
    /// backup than the newest, and says so whatever a lookup finds.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set drilled.</param>
    /// <param name="destinationName">The destination drilled.</param>
    /// <param name="newest">The newest backup the destination holds, hex.</param>
    /// <param name="restored">The backup the drill restored, hex.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    internal static async Task<string> RestoredInsteadAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, string newest, string restored,
        CancellationToken cancellationToken)
    {
        var newestName = $"backup {newest}";
        var restoredName = $"backup {restored}";
        var check = string.Empty;
        try
        {
            if (await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is { } archive)
            {
                string? newestKey;
                string? restoredKey;
                using (var catalogue = archive.OpenReadCatalogue())
                {
                    var setId = Convert.FromHexString(set.Id);
                    string? KeyOf(string wanted)
                    {
                        foreach (var row in catalogue.EnumerateSnapshots())
                        {
                            if (row.BackupSetId.Span.SequenceEqual(setId)
                                && Convert.ToHexStringLower(row.SnapshotId.Span) == wanted)
                            {
                                return Repository.Packing.MetadataStoreKeys.Snapshot(
                                    row.DeviceId.Span, row.BackupSetId.Span, row.SnapshotId.Span).Value;
                            }
                        }

                        return null;
                    }

                    newestKey = KeyOf(newest);
                    restoredKey = KeyOf(restored);
                }

                if (newestKey is not null)
                {
                    newestName = NewBackupProof.NameOf(archive, set, newestKey);
                    check = runtime.DestinationSync.Find(set.Id, destinationName) is { } ledger
                        ? CheckOf(ledger, newestKey)
                        : string.Empty;
                }

                if (restoredKey is not null)
                {
                    restoredName = NewBackupProof.NameOf(archive, set, restoredKey);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Named by identity rather than not said: the drill restored
            // another backup than the newest, which is never left unsaid.
        }

        return $"The newest backup, {newestName}, was not yet proven here{check}; the drill restored the newest backup proven here, {restoredName}.";
    }

    /// <summary>
    /// How far the proof of <paramref name="backup"/> had got at the
    /// destination, as a note's clause: empty where it is not the backup under
    /// proof there.
    /// </summary>
    private static string CheckOf(DestinationSyncRecord ledger, string backup) =>
        !string.Equals(ledger.ProvingSnapshot, backup, StringComparison.Ordinal)
            ? string.Empty
            : ledger switch
            {
                { ProvingFailedKey: { } failed } => $": its check had failed at {failed}",
                { ProvingBlobsProven: { } done, ProvingBlobsLeft: { } left } => string.Create(
                    CultureInfo.InvariantCulture, $": its check had proven {done} of {done + left} blobs"),
                _ => ": its check was under way",
            };

    /// <summary>
    /// What a passing drill states of the backup it drilled where the ledger
    /// knows that backup was not yet proven at the destination (ADR-0199
    /// Amendment 8): how far its proof there had got, and the newest backup
    /// proven there. Null where the ledger does not know it unproven there,
    /// by the rule a restore's plan follows (Amendment 6).
    /// </summary>
    /// <remarks>
    /// Said in the past tense: the drill's limit stands until the next drill,
    /// and the proof may end before then. The backups are named from the
    /// set's own catalogue; a backup it does not hold is not dated against,
    /// and a lookup that fails states nothing rather than fail the drill.
    /// </remarks>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set drilled.</param>
    /// <param name="destinationName">The destination drilled.</param>
    /// <param name="snapshotId">The backup drilled, hex.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    internal static async Task<string?> NotYetProvenAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, string snapshotId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (runtime.DestinationSync.Find(set.Id, destinationName) is not { } ledger
                || await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is not { } archive)
            {
                return null;
            }

            string? drilled = null;
            ulong drilledAt = 0;
            ulong? provenAt = null;
            using (var catalogue = archive.OpenReadCatalogue())
            {
                var setId = Convert.FromHexString(set.Id);
                var wanted = Convert.FromHexString(snapshotId);
                foreach (var row in catalogue.EnumerateSnapshots())
                {
                    if (!row.BackupSetId.Span.SequenceEqual(setId))
                    {
                        continue;
                    }

                    var key = Repository.Packing.MetadataStoreKeys.Snapshot(
                        row.DeviceId.Span, row.BackupSetId.Span, row.SnapshotId.Span).Value;
                    if (row.SnapshotId.Span.SequenceEqual(wanted))
                    {
                        drilled = key;
                        drilledAt = row.CapturedAt;
                    }

                    if (string.Equals(key, ledger.ProvenSnapshot, StringComparison.Ordinal))
                    {
                        provenAt = row.CapturedAt;
                    }
                }
            }

            if (drilled is null || !NewBackupProof.IsNotProven(ledger, drilled, drilledAt, provenAt))
            {
                return null;
            }

            var check = CheckOf(ledger, drilled);
            var standing = ledger.ProvenSnapshot is { } proven
                ? $"the newest backup proven here was {NewBackupProof.NameOf(archive, set, proven)}"
                : "no backup was proven here yet";
            return $"The backup drilled, {NewBackupProof.NameOf(archive, set, drilled)}, was not yet proven here{check}; {standing}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Plans one sampled file: the plan when the replica holds every object it
    /// needs, archived or not, and otherwise the drill's failure. A plan names
    /// what is missing by path, so the failure says only that something is.
    /// </summary>
    private static async Task<(RestorePlanResult? Plan, DrillOutcome? Failure)> PlanAsync(
        IFallbackPlanService handler, string snapshotId, string path, string sourceId, CancellationToken cancellationToken)
    {
        var planned = await handler.ExecuteAsync(
            new PlanRestoreCommand(snapshotId, path, Source: sourceId), cancellationToken).ConfigureAwait(false);
        switch (planned)
        {
            case ServiceError error:
                ThrowIfCancelled(error, "planning a sampled file");
                return (null, Failed($"a sampled file would not plan: {Unnamed(error.Message, path)}", path));

            case RestorePlanResult { MissingObjects.Count: 0 } plan:
                return (plan, null);

            case RestorePlanResult:
                return (null, Failed("a sampled file is missing objects at the replica.", path));

            default:
                return (null, Failed($"planning a sampled file answered {planned.GetType().Name}.", path));
        }
    }

    /// <summary>A drill that failed at <paramref name="path"/>, which the failure does not name.</summary>
    private static DrillOutcome Failed(string failure, string path) => new(0, 0, failure) { FailedPath = path };

    /// <summary>What the drill's notice says in place of the file it leaves out.</summary>
    private const string WithheldSentence =
        "Which file it was is shown only to someone who unlocks the set with its passphrase.";

    /// <summary>What a drill's words call the file they leave out.</summary>
    private const string SampledFile = "the sampled file";

    /// <summary>
    /// <paramref name="message"/> with <paramref name="path"/> taken out, so a
    /// drill's words can go where anyone signed in reads them (FR-WOR-007,
    /// ADR-0089 Amendment 1): the path as the snapshot names it, as this
    /// platform writes it, and the file's own name where it stands as a part
    /// of a path or in quotes. A name in running prose is not matched, since
    /// a short one would match inside other words.
    /// </summary>
    /// <param name="message">The engine's words.</param>
    /// <param name="path">The path the drill was at, or null.</param>
    internal static string Unnamed(string message, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return message;
        }

        var said = message.Replace(path, SampledFile, StringComparison.Ordinal);
        var native = path.Replace('/', Path.DirectorySeparatorChar);
        said = said.Replace(native, SampledFile, StringComparison.Ordinal);

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            return said;
        }

        var unnamed = new System.Text.StringBuilder(said.Length);
        var from = 0;
        for (var at = said.IndexOf(name, StringComparison.Ordinal); at >= 0;
            at = said.IndexOf(name, at + name.Length, StringComparison.Ordinal))
        {
            var end = at + name.Length;
            var opens = at > 0 && said[at - 1] is '/' or '\\' or '\'' or '"';
            var closes = end == said.Length || said[end] is '/' or '\\' or '\'' or '"' or ':' or ',' or ';' or ')'
                || char.IsWhiteSpace(said[end]);
            if (opens && closes)
            {
                unnamed.Append(said, from, at - from).Append(SampledFile);
                from = end;
            }
        }

        return unnamed.Append(said, from, said.Length - from).ToString();
    }

    /// <summary>
    /// The limit a drill states when the budget left files unsampled: how
    /// many, and what the budget was, so a reader can tell a small archive
    /// from a capped one.
    /// </summary>
    private static string CapLimit(int skipped, SampleBudget budget) =>
        $"{skipped} file(s) the descent reached were not sampled under the peer drill's byte cap "
        + $"({budget.FileCap / (1024 * 1024)} MiB per file, {budget.TotalCap / (1024 * 1024)} MiB per drill); "
        + "a file over the cap is proved by the manual drill, not on a cadence over a peer's link (ADR-0054 Amendment 3).";

    /// <summary>
    /// The limit a drill states when the store holds what sampled files need
    /// only in an archival tier (ADR-0105): how many, how far the road back
    /// was proved to them, and what a restore of them needs first.
    /// </summary>
    private static string ArchivedLimit(int archived) =>
        $"content archived: the store holds what {archived} sampled file(s) need only in an archival tier, which "
        + "serves nothing of it until it is rehydrated there, so the drill proved the road back to them as far as their "
        + "content — the replica opens, its index and catalogue rebuild, and the store holds every object they need — "
        + "and did not read it. A restore of them needs it rehydrated first (ADR-0105).";

    /// <summary>
    /// Whether a restore failed for no reason other than sealed content:
    /// nothing written, and every failure the engine reported — all of them,
    /// not a sample of a longer list — names <c>ContentSealed</c>.
    /// </summary>
    private static bool SealedOnly(RestoreResult result) =>
        result.Restored == 0
        && result.Failed > 0
        && result.FailedSample is { Count: > 0 } sample
        && sample.Count == result.Failed
        && sample.All(line => line.Contains("ContentSealed", StringComparison.Ordinal));

    /// <summary>
    /// Picks files to restore by descending the snapshot at random.
    /// </summary>
    /// <remarks>
    /// A random descent rather than a full enumeration, because a drill must
    /// cost the same on a snapshot of eleven files and one of eleven million.
    /// Random rather than first, because a fixed choice is one a damaged
    /// replica could survive for ever — the same reason the possession
    /// challenge draws its record at random.
    /// </remarks>
    private static async Task<(List<string> Paths, int Skipped)> SampleAsync(
        IFallbackPlanService handler,
        string sourceId,
        string snapshotId,
        SampleBudget? budget,
        Random draw,
        Action<string> keepPath,
        CancellationToken cancellationToken)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var chosenBytes = 0L;

        for (var attempt = 0; attempt < SampleFiles * 3 && chosen.Count < SampleFiles; attempt++)
        {
            var path = string.Empty;
            for (var depth = 0; depth < MaximumDepth; depth++)
            {
                if (path.Length > 0)
                {
                    keepPath(path);
                }

                var listed = await handler.ExecuteAsync(
                    new ListDirectoryCommand(snapshotId, path.Length == 0 ? null : path, sourceId),
                    cancellationToken).ConfigureAwait(false);

                // A listing that came back cancelled is not an empty folder.
                // Read as one, it ended every descent, and the drill blamed the
                // snapshot for having nothing it could sample.
                if (listed is ServiceError listFailure)
                {
                    ThrowIfCancelled(listFailure, path.Length == 0 ? "listing the snapshot" : "listing a folder of it");
                }

                if (listed is not DirectoryResult directory || directory.Entries.Count == 0)
                {
                    break;
                }

                // Drawn from what this drill has not already chosen or left: a
                // draw that lands on a file it already has spends an attempt,
                // and on a small snapshot that can be every attempt left,
                // leaving files the descent never reached. Directories stay on
                // offer, because the descent does not know which are spent.
                var offered = directory.Entries
                    .Where(candidate => candidate.Kind == "directory"
                        || !(seen.Contains(Child(path, candidate.Name)) || skipped.Contains(Child(path, candidate.Name))))
                    .ToList();
                if (offered.Count == 0)
                {
                    break;
                }

                var entry = offered[draw.Next(offered.Count)];
                var next = Child(path, entry.Name);
                if (entry.Kind == "directory")
                {
                    path = next;
                    continue;
                }

                // Only a file proves anything: a symlink or a device node
                // restores its record, not content, so a drill that sampled
                // one would pass without reading a segment.
                if (entry.Kind != "file")
                {
                    break;
                }

                // Under a budget, a file that would overrun it is left where
                // it is and the descent tries again — counted once, so the
                // limit names files and not attempts.
                if (budget is not null
                    && (entry.Length > budget.FileCap || chosenBytes + entry.Length > budget.TotalCap))
                {
                    skipped.Add(next);
                    break;
                }

                seen.Add(next);
                chosen.Add(next);
                chosenBytes += entry.Length;
                break;
            }
        }

        return (chosen, skipped.Count);
    }

    /// <summary>A snapshot path one entry below <paramref name="path"/>; the root is the empty path.</summary>
    private static string Child(string path, string name) => path.Length == 0 ? name : $"{path}/{name}";

    private static long BytesUnder(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Raises or clears the pair's drill notice. A recovery that would not
    /// work is the loudest thing this product has to say, and it has to be
    /// said before somebody needs it rather than during.
    /// </summary>
    private static void Announce(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        DrillOutcome outcome, ulong nowMs)
    {
        var key = $"drill-failed:{set.Id}:{destinationName}";
        if (outcome.Failure is null)
        {
            runtime.Notices.Resolve(key, nowMs);
            return;
        }

        runtime.Notices.Raise(
            key,
            $"A restore drill against '{destinationName}' could not bring back a file from set '{set.Name}': "
            + $"{outcome.Failure} The destination may still hold every byte it was sent — a drill tests the "
            + "path back, not the copy out — so check this before you need it."
            + (outcome.FailedPath is null ? string.Empty : " " + WithheldSentence),
            nowMs,
            outcome.FailedPath is null ? null : new NoticeNames(set.Id, [outcome.FailedPath]));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The restore cache is purged at start-up; a drill's scratch
            // outliving it by one run is not worth failing the drill over.
        }
    }
}
