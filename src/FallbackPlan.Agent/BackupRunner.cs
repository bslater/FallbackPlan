using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
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
    /// <param name="cancellationToken">Cancels the backup.</param>
    /// <returns>What happened.</returns>
    public static async ValueTask<BackupOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string jobId,
        DateTimeOffset now,
        bool full = false,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);

        var jobs = runtime.Jobs;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var progress = new BackupProgress(runtime.Progress, jobId);

        if (!Directory.Exists(set.Root))
        {
            // A vanished root may be an unmounted drive — recoverable; the next
            // pass retries (10 §3).
            var detail = $"root '{set.Root}' does not exist";
            jobs.Transition(jobId, JobState.FailedRecoverable, nowMs, detail);
            progress.Enter(JobState.FailedRecoverable);
            return new BackupOutcome(set.Name, "failed", detail);
        }

        try
        {
            jobs.Transition(jobId, JobState.Scanning, nowMs);
            progress.Enter(JobState.Scanning);

            // The set's staging archive, created on its first backup — staging
            // is internal, so nobody runs `init` for it (ADR-0034 §1).
            var archive = await runtime.ArchiveForAsync(set, cancellationToken).ConfigureAwait(false);

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

            var orchestrator = new PublicationOrchestrator(
                CapturePolicy.Default,
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
                progress);

            var snapshotId = RandomNumberGenerator.GetBytes(16);
            jobs.Transition(jobId, JobState.Publishing, nowMs);

            var published = await orchestrator.PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(),
                    RootPath = set.Root,
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

            jobs.Transition(
                jobId,
                outcome,
                nowMs,
                detail: partial ? $"partial: {published.Failures.Count} failure(s)" : null,
                snapshotId: Convert.ToHexString(snapshotId).ToLowerInvariant());
            progress.Enter(outcome);

            // The set-changed notice's condition is "the last backup predates
            // the settings", and this backup just captured under them
            // (ADR-0038). A no-op when no such notice stands.
            runtime.Notices.Resolve(SetChangeScan.NoticeKey(set.Id), nowMs);

            return new BackupOutcome(
                set.Name,
                "ran",
                $"{published.Files.Count} file(s), {published.Files.Count(file => file.Reused)} unchanged");
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
            // The staging archive refused to open — damage, or a passphrase
            // that no longer matches. Retrying cannot fix either; a human can
            // (10 §3).
            jobs.Transition(jobId, JobState.FailedPermanent, nowMs, exception.Message);
            progress.Enter(JobState.FailedPermanent);
            return new BackupOutcome(set.Name, "failed", exception.Message);
        }
    }

    /// <summary>
    /// Adapts the engine's progress reports to one job, carrying the terminal
    /// states the engine does not know about.
    /// </summary>
    private sealed class BackupProgress(ProgressHub hub, string jobId) : IJobProgressReporter
    {
        private JobProgress _latest = new(jobId, JobState.Pending, 0, 0, 0, 0, 0, 0);

        public void Report(JobProgress progress)
        {
            _latest = progress with { JobId = jobId };
            hub.Report(_latest);
        }

        public void Enter(JobState state) => Report(_latest with { State = state });
    }
}
