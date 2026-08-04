using System.Security.Cryptography;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>One set's outcome within a pass.</summary>
public sealed record AgentSetOutcome(string SetName, string Outcome, string? Detail);

/// <summary>What one Agent pass did.</summary>
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
/// One Agent pass (ADR-0027 §1): for every configured backup set, decide
/// due-or-not from the pure schedule arithmetic anchored at the last
/// COMPLETED job, run at most one backup per due set, and journal every
/// transition. Missed runs coalesce by construction — the pass asks
/// "is a run due", never "how many were missed". The pass is a plain
/// method so tests drive it directly; the daemon loop just calls it on an
/// interval.
/// </summary>
public sealed class AgentPass
{
    /// <summary>Runs one pass over the configuration in <paramref name="stateDirectory"/>.</summary>
    public static async ValueTask<AgentPassResult> RunAsync(
        string repositoryPath,
        Passphrase passphrase,
        string stateDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        var configuration = ClientConfiguration.Load(Path.Combine(stateDirectory, "config.json"));
        var state = LocalState.LoadOrCreate(stateDirectory);
        var jobs = JobStateStore.Open(stateDirectory);
        var outcomes = new List<AgentSetOutcome>();

        var store = new Storage.Local.LocalFileSystemObjectStore(repositoryPath);
        using var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, cancellationToken)
            .ConfigureAwait(false);
        using var catalogue = CatalogueDb.Open(
            Path.Combine(stateDirectory, "catalogue.db"), repository.RepositoryId);

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
                var broken = jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
                jobs.Transition(broken.Id, JobState.FailedPermanent, (ulong)now.ToUnixTimeMilliseconds(), defect);
                outcomes.Add(new AgentSetOutcome(set.Name, "failed", defect));
                continue;
            }

            var anchor = jobs.LastCompleted(set.Id) is { } completed
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)completed.UpdatedAt)
                : (DateTimeOffset?)null;

            if (!schedule!.IsDue(anchor, now))
            {
                outcomes.Add(new AgentSetOutcome(set.Name, "not-due", $"next: {schedule.NextRun(anchor, now):u}"));
                continue;
            }

            outcomes.Add(await RunSetAsync(set, repository, store, catalogue, state, jobs, stateDirectory, now, cancellationToken)
                .ConfigureAwait(false));
        }

        return new AgentPassResult(outcomes);
    }

    private static async ValueTask<AgentSetOutcome> RunSetAsync(
        BackupSetConfiguration set,
        OpenedRepository repository,
        Storage.Local.LocalFileSystemObjectStore store,
        CatalogueDb catalogue,
        LocalState state,
        JobStateStore jobs,
        string stateDirectory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var job = jobs.Begin(set.Id, nowMs);

        if (!Directory.Exists(set.Root))
        {
            // A vanished root may be an unmounted drive — recoverable; the
            // next pass retries (10 §3).
            jobs.Transition(job.Id, JobState.FailedRecoverable, nowMs, $"root '{set.Root}' does not exist");
            return new AgentSetOutcome(set.Name, "failed", $"root '{set.Root}' does not exist");
        }

        try
        {
            jobs.Transition(job.Id, JobState.Scanning, nowMs);

            var backupSetId = Convert.FromHexString(set.Id);
            var prior = catalogue.EnumerateSnapshots()
                .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

            var generation = repository.CurrentDataGeneration.Value >= repository.CurrentMetadataGeneration.Value
                ? repository.CurrentDataGeneration
                : repository.CurrentMetadataGeneration;

            var orchestrator = new PublicationOrchestrator(
                CapturePolicy.Default,
                repository.RepositoryId,
                Domain.Identifiers.WriterId.FromBytes(state.WriterId),
                generation,
                repository.Keys,
                repository.Hierarchy,
                store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(stateDirectory, "sequence.txt"))),
                Path.Combine(stateDirectory, "spool"),
                observer: null,
                catalogue);

            var snapshotId = RandomNumberGenerator.GetBytes(16);
            jobs.Transition(job.Id, JobState.Publishing, nowMs);

            var published = await orchestrator.PublishAsync(
                new SnapshotJob
                {
                    Source = new LocalFileSystemSource(),
                    RootPath = set.Root,
                    IncludeRules = set.IncludeRules,
                    ExcludeRules = set.ExcludeRules,
                    DeviceId = state.DeviceId,
                    BackupSetId = backupSetId,
                    SnapshotId = snapshotId,
                    ParentSnapshots = prior is null ? [] : [prior.SnapshotId],
                    PriorSnapshotId = prior?.SnapshotId,
                    NowUnixMilliseconds = nowMs,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = generation.Value + 2,
                    ClientVersion = "fallbackplan-agent/0.1",
                },
                cancellationToken).ConfigureAwait(false);

            jobs.Transition(
                job.Id, JobState.Complete, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                detail: published.ErrorManifestObjectId is null ? null : $"partial: {published.Failures.Count} failure(s)",
                snapshotId: Convert.ToHexString(snapshotId).ToLowerInvariant());

            return new AgentSetOutcome(
                set.Name, "ran",
                $"{published.Files.Count} file(s), {published.Files.Count(file => file.Reused)} unchanged");
        }
        catch (ArgumentException exception)
        {
            // Invalid rules or configuration: a human must fix it (10 §3).
            jobs.Transition(job.Id, JobState.FailedPermanent, nowMs, exception.Message);
            return new AgentSetOutcome(set.Name, "failed", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            jobs.Transition(job.Id, JobState.FailedRecoverable, nowMs, exception.Message);
            return new AgentSetOutcome(set.Name, "failed", exception.Message);
        }
    }
}
