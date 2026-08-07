using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Cli;

/// <summary>What a backup was asked to capture.</summary>
/// <remarks>
/// A configured set and an ad-hoc root are not interchangeable. A service knows
/// only what its configuration names, so <see cref="Root"/> is a direct-mode
/// capability — see <see cref="IOperationGateway.RunBackupAsync"/>.
/// </remarks>
public sealed record BackupRequest
{
    /// <summary>The configured set to run, or null for an ad-hoc root.</summary>
    public string? SetName { get; init; }

    /// <summary>The directory to capture when no set was named.</summary>
    public string? Root { get; init; }

    /// <summary>rules-v1 include rules, for an ad-hoc root.</summary>
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>rules-v1 exclude rules, for an ad-hoc root.</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];

    /// <summary>Whether to ignore the prior snapshot and re-read everything.</summary>
    public bool Full { get; init; }
}

/// <summary>What a backup did, and what to tell the operator about it.</summary>
/// <param name="Complete">Whether it finished with everything captured.</param>
/// <param name="Report">
/// The lines to print. The two gateways genuinely know different things — direct
/// mode holds the published snapshot in hand, a client holds a job the service
/// ran — so each renders what it observed rather than padding a shared shape
/// with values it would have to invent.
/// </param>
public sealed record BackupReport(bool Complete, IReadOnlyList<string> Report);

/// <summary>
/// Where a write command's work happens (ADR-0028 §3): a running service, or
/// this process in direct mode.
/// </summary>
/// <remarks>
/// The choice is resolved once, before any work, by
/// <see cref="OperationGateway.OpenForWriteAsync"/> — and it is never silent.
/// "Did my backup run against the same state the service uses" is a question an
/// operator must not have to guess at, so both modes announce themselves.
/// </remarks>
public interface IOperationGateway : IAsyncDisposable
{
    /// <summary>The mode line for the operator.</summary>
    string Mode { get; }

    /// <summary>Runs a backup wherever this gateway's work happens, and waits for it.</summary>
    /// <param name="request">What to capture.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="CliFailureException">The request is not one this gateway can serve.</exception>
    ValueTask<BackupReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves which gateway a command gets.</summary>
public static class OperationGateway
{
    /// <summary>
    /// Opens the gateway a write command needs: the service when one is
    /// listening, this process otherwise.
    /// </summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="forceDirect">
    /// Whether <c>--direct</c> was given. Forcing direct does not force the
    /// writer role free: if a service holds it this is refused naming the
    /// holder, because the alternative is two processes writing as one writer.
    /// </param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The gateway; dispose to release whatever it holds.</returns>
    public static async ValueTask<IOperationGateway> OpenForWriteAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        bool forceDirect,
        CancellationToken cancellationToken)
    {
        // The session opens without the role first, because the default state
        // directory is derived from the repository id — so the address a client
        // would connect to is not knowable until the repository is open.
        var session = await CliSession.OpenAsync(
            repoPath, passphraseEnvironmentVariable, stateDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!forceDirect)
            {
                // A socket that accepts is the only proof of life that matters;
                // there is no separate liveness protocol to consult.
                LocalServiceClient? client = null;
                try
                {
                    client = await LocalServiceClient.ConnectAsync(
                        session.StateDirectory, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceConnectionException)
                {
                    // Nothing is listening. Direct mode below, which is the
                    // ordinary case on a machine with no service installed.
                }

                if (client is not null)
                {
                    var address = session.StateDirectory;
                    session.Dispose();
                    return new ServiceGateway(client, address);
                }
            }

            session.TakeWriterRole();
            return new DirectGateway(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

/// <summary>The gateway that sends work to a running service.</summary>
internal sealed class ServiceGateway(LocalServiceClient client, string stateDirectory) : IOperationGateway
{
    /// <summary>How often to ask the service whether the job has finished.</summary>
    /// <remarks>
    /// Polling rather than watching: the progress stream is a separate
    /// connection carrying observations that may be dropped when a watcher falls
    /// behind (ADR-0029 §5), so it is the wrong thing to derive "has it
    /// finished" from. The job list is the authoritative answer.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc/>
    public string Mode =>
        $"mode: service — the service holding the writer role for '{stateDirectory}' will run this.";

    /// <inheritdoc/>
    public async ValueTask<BackupReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A service runs what its configuration names. An ad-hoc root is not
        // something it can be asked for, and quietly running it here instead
        // would be direct mode by the back door — against state the service
        // owns, which is the whole hazard the writer role exists to stop.
        if (request.SetName is null)
        {
            throw new CliFailureException(
                "A service is running and holds the writer role, so this backup would be run by the service — but a "
                + "service can only run a configured backup set, not an ad-hoc directory. Pass --set <name>, or stop "
                + "the service and re-run with --direct.");
        }

        var accepted = await client.ExecuteAsync(
            new RunBackupCommand(request.SetName, request.Full), cancellationToken).ConfigureAwait(false);

        var jobId = accepted switch
        {
            JobAcceptedResult job => job.JobId,
            ServiceError error => throw new CliFailureException($"The service refused the backup: {error.Message}"),
            _ => throw new CliFailureException($"The service answered a backup with {accepted.GetType().Name}."),
        };

        var finished = await AwaitJobAsync(jobId, cancellationToken).ConfigureAwait(false);

        List<string> report =
        [
            $"job            {finished.Id}",
            $"snapshot id    {finished.SnapshotId ?? "(none committed)"}",
            $"status         {Describe(finished.State)}",
        ];

        if (!string.IsNullOrWhiteSpace(finished.Detail))
        {
            report.Add($"detail         {finished.Detail}");
        }

        return new BackupReport(finished.State == JobState.Complete, report);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => client.DisposeAsync();

    private static string Describe(JobState state) => state switch
    {
        JobState.Complete => "complete",
        JobState.Cancelled => "cancelled",
        JobState.Paused => "PAUSED — resumable; the service will not finish it unattended",
        JobState.FailedRecoverable => "FAILED (recoverable) — the service retries on its next pass",
        JobState.FailedPermanent => "FAILED — needs intervention; it will not be retried",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>Whether a job has stopped moving on its own.</summary>
    private static bool HasSettled(JobState state) => state is
        JobState.Complete or JobState.Cancelled or JobState.Paused
        or JobState.FailedRecoverable or JobState.FailedPermanent;

    private async ValueTask<JobDescriptor> AwaitJobAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var listed = await client.ExecuteAsync(new ListJobsCommand(false), cancellationToken).ConfigureAwait(false);
            if (listed is ServiceError error)
            {
                throw new CliFailureException($"The service stopped reporting the job: {error.Message}");
            }

            if (listed is not JobsResult jobs)
            {
                throw new CliFailureException($"The service answered a job list with {listed.GetType().Name}.");
            }

            var job = jobs.Jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal))
                ?? throw new CliFailureException($"The service forgot job '{jobId}' before it finished.");

            if (HasSettled(job.State))
            {
                return job;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The gateway that does the work in this process, holding the writer role.</summary>
internal sealed class DirectGateway(CliSession session) : IOperationGateway
{
    /// <summary>The session this gateway works through — the verbs a service cannot serve still need it.</summary>
    public CliSession Session => session;

    /// <inheritdoc/>
    public string Mode => $"mode: direct — this command holds the writer role for '{session.StateDirectory}'.";

    /// <inheritdoc/>
    public async ValueTask<BackupReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string rootPath;
        IReadOnlyList<string> include, exclude;
        byte[] backupSetId;

        if (request.SetName is { } setName)
        {
            var configuration = ClientConfiguration.Load(session.ConfigurationPath);
            var set = configuration.FindSet(setName)
                ?? throw new CliFailureException($"No backup set named '{setName}' exists in {session.ConfigurationPath}.");
            rootPath = set.Root;
            include = set.IncludeRules;
            exclude = set.ExcludeRules;
            backupSetId = Convert.FromHexString(set.Id);
        }
        else
        {
            rootPath = request.Root ?? throw new CliFailureException("Pass a root directory or --set <name>.");
            include = request.IncludeRules;
            exclude = request.ExcludeRules;
            backupSetId = session.BackupSetId;
        }

        if (!Directory.Exists(rootPath))
        {
            throw new CliFailureException($"'{rootPath}' is not a directory.");
        }

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);

        // Incremental against the newest snapshot of the same set the catalogue
        // knows — unless --full asks for a re-read.
        var prior = request.Full
            ? null
            : catalogue.EnumerateSnapshots()
                .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

        var orchestrator = new PublicationOrchestrator(
            CapturePolicy.Default,
            session.Repository.RepositoryId,
            session.Writer,
            session.CurrentGeneration,
            session.Repository.Keys,
            session.Repository.Hierarchy,
            session.Store,
            session.CreateSequence(),
            session.SpoolDirectory,
            observer: null,
            catalogue);

        var snapshotId = RandomNumberGenerator.GetBytes(16);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var published = await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = new FallbackPlan.Filesystem.Local.LocalFileSystemSource(),
                RootPath = rootPath,
                IncludeRules = include,
                ExcludeRules = exclude,
                DeviceId = session.DeviceId,
                BackupSetId = backupSetId,
                SnapshotId = snapshotId,
                ParentSnapshots = prior is null ? [] : [prior.SnapshotId],
                PriorSnapshotId = prior?.SnapshotId,
                NowUnixMilliseconds = now,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = session.CurrentGeneration.Value + 2,
                ClientVersion = "fallbackplan-cli/0.1",
            },
            cancellationToken).ConfigureAwait(false);

        session.State.RecordJob(new JobHistoryEntry
        {
            SnapshotId = Hex(snapshotId),
            BackupSetId = Hex(backupSetId),
            StartedAt = now,
            CaptureStatus = (byte)(published.ErrorManifestObjectId is null ? 1 : 2),
            Files = published.Files.Count,
            Failures = published.Failures.Count,
        });

        var reused = published.Files.Count(file => file.Reused);
        List<string> report =
        [
            $"snapshot id    {Hex(snapshotId)}",
            string.Create(CultureInfo.InvariantCulture,
                $"files          {published.Files.Count} ({reused} unchanged, {published.Failures.Count} failed)"),
            string.Create(CultureInfo.InvariantCulture, $"data blobs     {published.ContentBlobs.Count} new"),
        ];

        if (prior is not null)
        {
            report.Add($"incremental    against {Hex(prior.SnapshotId)}");
        }

        report.Add(published.ErrorManifestObjectId is null
            ? "status         complete"
            : "status         PARTIAL — see the error manifest");

        return new BackupReport(published.ErrorManifestObjectId is null, report);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();
}
