using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

/// <summary>One backup's outcome, as the service reports it.</summary>
/// <param name="SetName">The set that ran.</param>
/// <param name="Outcome">One of <c>ran</c>, <c>failed</c>, <c>cancelled</c>.</param>
/// <param name="Detail">What to tell the operator.</param>
public sealed record BackupOutcome(string SetName, string Outcome, string? Detail);

/// <summary>
/// Runs one backup set against the service's shared repository, catalogue and
/// writer sequence.
/// </summary>
/// <remarks>
/// This used to construct its own <c>WriterSequence</c> per set per poll, which
/// is precisely the shape ADR-0028 found unsafe: a sequence allocator that
/// loads its file once at construction hands out numbers another instance is
/// also handing out. There is now one allocator, owned by the service, and this
/// borrows it.
/// </remarks>
public static class BackupRunner
{
    /// <summary>Runs one set and journals every transition.</summary>
    /// <param name="runtime">The service's shared state.</param>
    /// <param name="set">The set to run.</param>
    /// <param name="jobId">The journal entry to transition.</param>
    /// <param name="now">The clock, passed in so the caller decides it.</param>
    /// <param name="full">Whether to ignore prior versions and re-capture everything.</param>
    /// <param name="pauseGate">
    /// The run's suspension point (ADR-0047 Amendment 1), when its scheduler preempts;
    /// the capture pipeline honours it between scan events.
    /// </param>
    /// <param name="cancellationToken">Cancels the backup.</param>
    /// <returns>What happened.</returns>
    public static async ValueTask<BackupOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string jobId,
        DateTimeOffset now,
        bool full = false,
        IPauseGate? pauseGate = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);

        var jobs = runtime.Jobs;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var progress = new BackupProgress(runtime.Progress, jobId);

        // A suspension must reach progress watchers, not only the journal
        // (ADR-0047 Amendment 2). The paused report re-emits the run's live
        // counts, so a watching card keeps its meter; on resume the state it
        // parked from is restored, and the next scan event refreshes it.
        if (pauseGate is PauseGate gate)
        {
            var resumeTo = JobState.Scanning;
            gate.AddCallbacks(
                onParked: () =>
                {
                    resumeTo = progress.LastState;
                    progress.Enter(JobState.Paused);
                },
                onResumed: () => progress.Enter(resumeTo));
        }

        // Every root must be there, or the run refuses: capturing a snapshot
        // silently missing a whole labelled subtree would make everything
        // under it read "deleted" (ADR-0040). A vanished root may be an
        // unmounted drive — recoverable; the next pass retries (10 §3).
        var missing = set.Roots.Where(root => !Directory.Exists(root.Path)).Select(root => root.Path).ToList();
        if (missing.Count > 0)
        {
            var detail = missing.Count == 1
                ? $"root '{missing[0]}' does not exist"
                : $"roots do not exist: '{string.Join("', '", missing)}'";
            jobs.Transition(jobId, JobState.FailedRecoverable, nowMs, detail);
            progress.Enter(JobState.FailedRecoverable);
            return new BackupOutcome(set.Name, "failed", detail);
        }

        DestinationShipSink? sink = null;
        var runCommitted = false;
        try
        {
            jobs.Transition(jobId, JobState.Scanning, nowMs);
            progress.Enter(JobState.Scanning);

            // The set's archive — staging, or a direct-ship sink over the
            // metadata store — created on its first backup; either way it is
            // internal, so nobody runs `init` for it (ADR-0034 §1, ADR-0046).
            var archive = await runtime.ArchiveForAsync(set, cancellationToken).ConfigureAwait(false);

            // A direct-ship run (ADR-0046) resolves its destinations before a
            // byte moves: with no staging archive, a capture with nowhere to
            // ship refuses here (an IOException, recoverable — the next pass
            // retries once a destination returns).
            if (archive.ShipSink is { } shipSink)
            {
                sink = shipSink;
                await shipSink.BeginRunAsync(set, nowMs, cancellationToken).ConfigureAwait(false);
            }

            // A full run empties both the parent list and the incremental
            // baseline, exactly as direct mode does — the flag was accepted
            // over the service and silently dropped before ADR-0038.
            var backupSetId = Convert.FromHexString(set.Id);
            var prior = full
                ? null
                : archive.Catalogue.EnumerateSnapshots()
                    .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

            var generation =
                archive.Repository.CurrentDataGeneration.Value >= archive.Repository.CurrentMetadataGeneration.Value
                    ? archive.Repository.CurrentDataGeneration
                    : archive.Repository.CurrentMetadataGeneration;

            // A write-only archive takes the device trust domain (ADR-0042):
            // the repository domain's verify-on-reuse reads content, which a
            // write-only holder cannot, and the orchestrator refuses the
            // combination by name rather than degrading silently. A
            // direct-ship set does the same (ADR-0046): the content sits at
            // destinations, and verify-on-reuse pulling ranges back over the
            // sink would pay a destination round trip per reuse to re-check
            // bytes the catalogue already vouches for.
            var policy = archive.Repository.Keys.WriteOnly || archive.ShipSink is not null
                ? CapturePolicy.Default with { DedupTrustDomain = DedupTrustDomain.Device }
                : CapturePolicy.Default;

            var orchestrator = new PublicationOrchestrator(
                policy,
                archive.Repository.RepositoryId,
                runtime.Writer,
                generation,
                archive.Repository.Keys,
                archive.Repository.Hierarchy,
                archive.Store,
                archive.Sequence,
                archive.SpoolDirectory,
                observer: null,
                archive.Catalogue,
                progress,
                runtime.LoggerFor<PublicationOrchestrator>());

            var snapshotId = RandomNumberGenerator.GetBytes(16);
            jobs.Transition(jobId, JobState.Publishing, nowMs);

            var published = await orchestrator.PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(runtime.LoggerFor<LocalFileSystemSource>()),
                    Roots = SetChangeScan.ScanRootsOf(set),
                    IncludeRules = set.IncludeRules,
                    ExcludeRules = set.ExcludeRules,
                    DeviceId = runtime.State.DeviceId,
                    BackupSetId = backupSetId,
                    SnapshotId = snapshotId,
                    ParentSnapshots = prior is null ? [] : [prior.SnapshotId],
                    PriorSnapshotId = prior?.SnapshotId,
                    NowUnixMilliseconds = nowMs,
                    // The pass clock above keeps schedule derivation pure; the
                    // capture-completion stamp wants the time capture actually
                    // finished, which only a live clock can say.
                    Clock = static () => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = generation.Value + 2,
                    ClientVersion = "fallbackplan-agent/0.1",
                    PauseGate = pauseGate,
                },
                cancellationToken).ConfigureAwait(false);

            // The pass's clock, not the wall clock. This transition writes the
            // anchor the scheduler reads back as "last completed", so reading it
            // from UtcNow while due-ness is judged against the caller's `now`
            // compares two different clocks — and the answer is only right while
            // they happen to agree. A caller that injects a clock got a job
            // stamped in its own future and its next run never came due.
            // A snapshot that could not capture everything committed anyway,
            // and says so in the state rather than only in the detail. The
            // detail stays as the human half — the count — but it is no longer
            // the only thing distinguishing this run from a clean one.
            var partial = published.ErrorManifestObjectId is not null;
            var outcome = partial ? JobState.CompletedWithFailures : JobState.Complete;

            // Always an explicit detail: Transition keeps the prior detail on
            // null, and a preempted run's prior detail is "resumed" — which
            // must not survive onto the terminal record a person reads.
            var summary = $"{published.Files.Count} file(s), {published.Files.Count(file => file.Reused)} unchanged";
            jobs.Transition(
                jobId,
                outcome,
                nowMs,
                detail: partial ? $"partial: {published.Failures.Count} failure(s)" : summary,
                snapshotId: Convert.ToHexString(snapshotId).ToLowerInvariant());
            progress.Enter(outcome);
            runCommitted = true;

            // The set-changed notice's condition is "the last backup predates
            // the settings", and this backup just captured under them
            // (ADR-0038). A no-op when no such notice stands.
            runtime.Notices.Resolve(SetChangeScan.NoticeKey(set.Id), nowMs);

            return new BackupOutcome(set.Name, "ran", summary);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a command, not a signal (ADR-0029 §4). Before
            // this, an interrupted job stayed `Publishing` for ever — and the
            // sequences it had allocated stayed pending, which is correct: the
            // next publication discharges them as void deltas, exactly as it
            // would after a crash.
            jobs.Transition(jobId, JobState.Cancelled, nowMs, "cancelled by request");
            progress.Enter(JobState.Cancelled);
            return new BackupOutcome(set.Name, "cancelled", "cancelled by request");
        }
        catch (ArgumentException exception)
        {
            // Invalid rules or configuration: a human must fix it (10 §3).
            jobs.Transition(jobId, JobState.FailedPermanent, nowMs, exception.Message);
            progress.Enter(JobState.FailedPermanent);
            return new BackupOutcome(set.Name, "failed", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            jobs.Transition(jobId, JobState.FailedRecoverable, nowMs, exception.Message);
            progress.Enter(JobState.FailedRecoverable);
            return new BackupOutcome(set.Name, "failed", exception.Message);
        }
        catch (Exception exception) when (exception is RepositoryOpenException or Repository.Crypto.KeyUnwrapFailedException)
        {
            // The set's archive refused to open — damage, or a passphrase
            // that no longer matches. Retrying cannot fix either; a human can
            // (10 §3).
            jobs.Transition(jobId, JobState.FailedPermanent, nowMs, exception.Message);
            progress.Enter(JobState.FailedPermanent);
            return new BackupOutcome(set.Name, "failed", exception.Message);
        }
        finally
        {
            // The run's books close however the run ended (ADR-0046 §3): a
            // failed or cancelled run still owes the ledger its drops and
            // skips — without them no back-off arms and the healing catch-up
            // never schedules — and the run's read scope is released so a
            // destination plugged back in answers without a restart.
            // Successes are recorded only when the snapshot committed.
            sink?.CompleteRun(nowMs, runCommitted);
        }
    }

    /// <summary>
    /// Adapts the engine's progress reports to one job, carrying the terminal
    /// states the engine does not know about.
    /// </summary>
    private sealed class BackupProgress(ProgressHub hub, string jobId) : IJobProgressReporter
    {
        private JobProgress _latest = new(jobId, JobState.Pending, 0, 0, 0, 0, 0, 0);

        public JobState LastState => _latest.State;

        public void Report(JobProgress progress)
        {
            _latest = progress with { JobId = jobId };
            hub.Report(_latest);
        }

        public void Enter(JobState state) => Report(_latest with { State = state });
    }
}
