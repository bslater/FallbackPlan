using Bodu;
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
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using RestoreResult = FallbackPlan.Api.RestoreResult;
using FallbackPlan.Cli.Resources;

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

/// <summary>What an operation did, and what to tell the operator about it.</summary>
/// <param name="Ok">Whether it succeeded outright; false sets a non-zero exit code.</param>
/// <param name="Lines">
/// The lines to print. The two gateways genuinely know different things — direct
/// mode holds the published snapshot or the failing blob in hand, a client holds
/// what the contract carries back — so each renders what it observed rather than
/// padding a shared shape with values it would have to invent.
/// </param>
public sealed record OperationReport(bool Ok, IReadOnlyList<string> Lines);

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
    ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken);

    /// <summary>Verifies stored blobs at the given level.</summary>
    /// <param name="level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>What it found.</returns>
    ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken);

    /// <summary>Reports repository health at the given verification level.</summary>
    /// <param name="level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What it found.</returns>
    ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken);

    /// <summary>Restores a snapshot, or a path within it.</summary>
    /// <param name="request">What to restore and where.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What it wrote.</returns>
    ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Converges destinations now, outside the schedule (FR-DEST-002). Only a
    /// service can serve this — the fan-out needs its scheduler, ledger and
    /// staging archives — so direct mode refuses with directions.
    /// </summary>
    /// <param name="setName">The set to sync, or null for every configured set.</param>
    /// <param name="destinationName">The destination to sync, or null for each set's every destination.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Where each pair stands, from the refreshed ledger.</returns>
    ValueTask<OperationReport> SyncAsync(string? setName, string? destinationName, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a retention pass per configured set (architecture 07). Only a
    /// service can serve this — retention needs the whole configuration, the
    /// sync ledger and the writer role — so direct mode refuses with
    /// directions.
    /// </summary>
    /// <param name="apply">False reports only; true tombstones, sweeps and trims (FR-GC-005).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The report, per set in configuration order.</returns>
    ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads a destination's stored objects and confirms they still match
    /// what was sealed (FR-VER-002, FR-VER-004).
    /// </summary>
    /// <param name="setName">The set to verify; null takes every configured set.</param>
    /// <param name="destinationName">The destination to verify; null takes each set's every destination.</param>
    /// <param name="full">Whether to keep going until every object has been read.</param>
    /// <param name="probe">
    /// Whether to read nothing and confirm only that the destination could
    /// take a backup — the depth that answers before the first sync.
    /// </param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken);
}

/// <summary>What a restore was asked to write, and where.</summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore, or null for the whole snapshot.</param>
/// <param name="OutputDirectory">
/// Where to write. In service mode this is a path on the machine running the
/// service (ADR-0028 §6) — the same machine, on the local binding.
/// </param>
public sealed record RestoreRequest(string SnapshotId, string? Path, string OutputDirectory);

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
                    return new ServiceGateway(client, LocalMode(address), client);
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

    /// <summary>
    /// Opens the gateway a read command needs: the service when one is
    /// listening, this process otherwise — and without the writer role either
    /// way.
    /// </summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="forceDirect">Whether <c>--direct</c> was given.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The gateway; dispose to release whatever it holds.</returns>
    /// <remarks>
    /// A read path never takes the writer role, so unlike the write gateway
    /// this one is never refused for holding it. Routing to a running service
    /// is still worth doing — one process then owns the reads, against one
    /// catalogue connection, bounded by the queue rather than by luck — but
    /// falling back to reading directly is correct rather than a concession.
    /// </remarks>
    public static async ValueTask<IOperationGateway> OpenForReadAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        bool forceDirect,
        CancellationToken cancellationToken)
    {
        var session = await CliSession.OpenAsync(
            repoPath, passphraseEnvironmentVariable, stateDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!forceDirect)
            {
                LocalServiceClient? client = null;
                try
                {
                    client = await LocalServiceClient.ConnectAsync(
                        session.StateDirectory, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceConnectionException)
                {
                    // Nothing listening; read it here.
                }

                if (client is not null)
                {
                    var address = session.StateDirectory;
                    session.Dispose();
                    return new ServiceGateway(client, LocalMode(address), client);
                }
            }

            return new DirectGateway(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the gateway a command needs against a <em>remote</em> paired service
    /// (ADR-0028 §5): dial the endpoint, authenticate as the pinned peer, and
    /// carry the command contract over the opened session.
    /// </summary>
    /// <param name="host">The service's host.</param>
    /// <param name="port">The service's remote-binding port.</param>
    /// <param name="stateDirectory">The console's state directory (its peer identity and pairings).</param>
    /// <param name="fingerprint">The fingerprint of the pinned service to expect.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The gateway; dispose to close the session and release the device key.</returns>
    /// <remarks>
    /// There is no direct-mode fallback here, deliberately: a remote console does
    /// not hold the repository, so if the paired service cannot be reached the
    /// only honest answer is to say so — see <see cref="RemotePeer.ConnectAsync"/>,
    /// which turns a refusal into a stated failure.
    /// </remarks>
    public static async ValueTask<IOperationGateway> OpenForRemoteAsync(
        string host,
        int port,
        string stateDirectory,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var connection = await RemotePeer.ConnectAsync(
            host, port, stateDirectory, fingerprint, "fallbackplan-cli", cancellationToken).ConfigureAwait(false);

        return new ServiceGateway(connection.Client, RemoteMode(host, port), connection);
    }

    private static string LocalMode(string stateDirectory) =>
        $"mode: service — the service holding the writer role for '{stateDirectory}' will run this.";

    private static string RemoteMode(string host, int port) =>
        $"mode: service (remote) — the paired service at {host}:{port} will run this.";
}

/// <summary>The gateway that sends work to a running service.</summary>
/// <remarks>
/// The same gateway serves both bindings. Over the local binding
/// <paramref name="client"/> is a <see cref="LocalServiceClient"/>; over the
/// remote binding it is a <see cref="RemoteServiceClient"/> — the body only ever
/// speaks the <see cref="IFallbackPlanClient"/> contract, so nothing here knows
/// or cares which. <paramref name="owned"/> is what closing the gateway
/// disposes: on the local binding that is the client itself; on the remote
/// binding it is the connection holder that also releases the device key.
/// </remarks>
internal sealed class ServiceGateway(IFallbackPlanClient client, string mode, IAsyncDisposable owned) : IOperationGateway
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
    public string Mode => mode;

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // A service runs what its configuration names. An ad-hoc root is not
        // something it can be asked for, and quietly running it here instead
        // would be direct mode by the back door — against state the service
        // owns, which is the whole hazard the writer role exists to stop.
        if (request.SetName is null)
        {
            throw new CliFailureException(Strings.ServiceGateway_ServiceRunningHoldsWriterRole);
        }

        var accepted = await client.ExecuteAsync(
            new RunBackupCommand(request.SetName, request.Full), cancellationToken).ConfigureAwait(false);

        var jobId = accepted switch
        {
            JobAcceptedResult job => job.JobId,
            ServiceError error => throw new CliFailureException(Strings.FormatServiceGateway_ServiceRefusedBackup(error.Message)),
            _ => throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredBackupWith(accepted.GetType().Name)),
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

        return new OperationReport(finished.State == JobState.Complete, report);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken)
    {
        var result = await SendAsync<VerificationResult>(
            new VerifyCommand(level), "a verification", cancellationToken).ConfigureAwait(false);

        return new OperationReport(
            result.Failures == 0,
            [
                string.Create(CultureInfo.InvariantCulture,
                    $"verified {result.ObjectsChecked} blob(s) at level {result.Level}; {result.Failures} failure(s)"),
            ]);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken)
    {
        var result = await SendAsync<CheckResult>(
            new CheckCommand(level), "a check", cancellationToken).ConfigureAwait(false);

        List<string> lines = [.. result.Findings.Select(finding => $"finding: {finding}")];
        lines.Add(result.Findings.Count == 0
            ? "check: OK"
            : string.Create(CultureInfo.InvariantCulture, $"check: {result.Findings.Count} problem(s)"));

        return new OperationReport(result.Findings.Count == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        var result = await SendAsync<RestoreResult>(
            new RunRestoreCommand(request.SnapshotId, request.Path, request.OutputDirectory),
            "a restore",
            cancellationToken).ConfigureAwait(false);

        return new OperationReport(
            result.Failed == 0,
            [
                string.Create(CultureInfo.InvariantCulture,
                    $"restored {result.Restored} file(s) to {result.OutputDirectory}; {result.Failed} failure(s)"),
            ]);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken)
    {
        var result = await SendAsync<VerifyDestinationResult>(
            new VerifyDestinationCommand(setName, destinationName, full, probe), "a destination verification",
            cancellationToken).ConfigureAwait(false);

        // Unlike a sync, this one CAN fail: damaged objects are a finding, and
        // the exit code must carry that without anyone parsing the prose.
        return new OperationReport(result.Damaged == 0, result.Lines);
    }

    public async ValueTask<OperationReport> SyncAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken)
    {
        var result = await SendAsync<SyncResult>(
            new SyncCommand(setName, destinationName), "a sync", cancellationToken).ConfigureAwait(false);

        return new OperationReport(true, result.Lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken)
    {
        var result = await SendAsync<RetentionResult>(
            new RetentionCommand(apply), "a retention pass", cancellationToken).ConfigureAwait(false);

        return new OperationReport(true, result.Lines);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => owned.DisposeAsync();

    /// <summary>Sends one command and insists on the result type it should answer with.</summary>
    private async ValueTask<T> SendAsync<T>(ServiceCommand command, string what, CancellationToken cancellationToken)
        where T : ServiceResult
    {
        var result = await client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        return result switch
        {
            T expected => expected,
            ServiceError error => throw new CliFailureException(Strings.FormatServiceGateway_ServiceRefused(what, error.Message)),
            _ => throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredWith(what, result.GetType().Name)),
        };
    }

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
                throw new CliFailureException(Strings.FormatServiceGateway_ServiceStoppedReportingJob(error.Message));
            }

            if (listed is not JobsResult jobs)
            {
                throw new CliFailureException(Strings.FormatServiceGateway_ServiceAnsweredJobListWith(listed.GetType().Name));
            }

            var job = jobs.Jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal))
                ?? throw new CliFailureException(Strings.FormatServiceGateway_ServiceForgotJobBeforeFinished(jobId));

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
    public async ValueTask<OperationReport> RunBackupAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        string rootPath;
        IReadOnlyList<string> include, exclude;
        byte[] backupSetId;

        if (request.SetName is { } setName)
        {
            var configuration = ClientConfiguration.Load(session.ConfigurationPath);
            var set = configuration.FindSet(setName)
                ?? throw new CliFailureException(Strings.FormatDirectGateway_NoBackupSetNamedExists(setName, session.ConfigurationPath));
            rootPath = set.Root;
            include = set.IncludeRules;
            exclude = set.ExcludeRules;
            backupSetId = Convert.FromHexString(set.Id);
        }
        else
        {
            rootPath = request.Root ?? throw new CliFailureException(Strings.DirectGateway_PassRootDirectorySetName);
            include = request.IncludeRules;
            exclude = request.ExcludeRules;
            backupSetId = session.BackupSetId;
        }

        if (!Directory.Exists(rootPath))
        {
            throw new CliFailureException(Strings.FormatDirectGateway_NotDirectory(rootPath));
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
                Clock = static () => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

        return new OperationReport(published.ErrorManifestObjectId is null, report);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> VerifyAsync(string level, CancellationToken cancellationToken)
    {
        var parsed = ParseLevel(level);

        using var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        var blobs = 0;
        var failures = 0;
        List<string> lines = [];

        await foreach (var entry in session.Store
            .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            blobs++;
            var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, parsed, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                failures++;
                lines.Add($"FAILED {entry.Key.Value}: {result.Detail}");
            }
        }

        // Direct mode names the blob that failed. The contract carries only a
        // count, so this is one of the places the two paths legitimately differ
        // in what they can say.
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"verified {blobs} blob(s) at level {parsed}; {failures} failure(s)"));

        return new OperationReport(failures == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> CheckAsync(string level, CancellationToken cancellationToken)
    {
        var parsed = ParseLevel(level);
        var problems = 0;
        List<string> lines = [];

        using (var verifier = new VerifyEngine(session.Repository.RepositoryId, session.Repository.Keys, session.Store))
        {
            var blobs = 0;
            await foreach (var entry in session.Store
                .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
            {
                blobs++;
                var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, parsed, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok)
                {
                    problems++;
                    lines.Add($"blob FAILED  {entry.Key.Value}: {result.Detail}");
                }
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"blobs      {blobs} verified at {parsed}"));
        }

        using (var journalReader = new JournalReader(
            session.Store, session.Repository.RepositoryId, session.Repository.Hierarchy))
        {
            var (records, unparseable, journalFindings) = await journalReader
                .LoadAsync(session.CurrentGeneration.Value, cancellationToken).ConfigureAwait(false);
            problems += unparseable + journalFindings.Count;

            var survey = IntentSurveyor.Survey(
                records, unparseable, session.CurrentGeneration.Value,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), skewMarginMs: 300_000);

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"journal    {records.Count} record(s), {unparseable} unparseable, {survey.LiveIntents.Count} live intent(s)"));
            lines.AddRange(journalFindings.Select(finding => $"journal    {finding.Kind}: {finding.Detail}"));
        }

        if (File.Exists(session.CataloguePath))
        {
            using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);
            var findings = catalogue.Findings();
            problems += findings.Count;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"catalogue  {findings.Count} damage finding(s)"));
            lines.AddRange(findings.Select(finding => $"catalogue  {finding.Kind}: {finding.Detail}"));
        }
        else
        {
            lines.Add("catalogue  absent (rebuildable cache — run `rebuild-index` to materialise it)");
        }

        lines.Add(problems == 0
            ? "check: OK"
            : string.Create(CultureInfo.InvariantCulture, $"check: {problems} problem(s)"));

        return new OperationReport(problems == 0, lines);
    }

    /// <inheritdoc/>
    public async ValueTask<OperationReport> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(request);

        // Direct mode restores through the same planner and executor the
        // service uses (ADR-0028 §3: "the same operation performs identically
        // through either path"). The earlier hand-rolled walk here combined
        // repository-supplied path text with Path.Combine and wrote in place —
        // no containment, no quarantine, no receipt, no metadata — so a
        // manifest naming `../` escaped the output directory. Containment is a
        // property of the executor (architecture 08 §3), and this is now that
        // executor.
        var snapshotId = Convert.FromHexString(request.SnapshotId);
        var target = RestoreTargetProfile.ForLocalPlatform();

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);
        var plan = RestorePlanner.Plan(catalogue, snapshotId, request.Path ?? string.Empty, target);
        if (plan.Items.Count == 0)
        {
            throw new CliFailureException(Strings.FormatDirectGateway_CatalogueKnowsNothingUnderSnapshot(request.SnapshotId));
        }

        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan,
            request.OutputDirectory,
            new RestoreExecutionOptions
            {
                // The user named the output directory, so content lands there
                // rather than in quarantine — quarantine is the default when a
                // service restores onto a live machine it did not choose
                // (architecture 08 §3.1), which is a distinct control from
                // where a person's explicit `--output` points. Containment,
                // the receipt and metadata come from the executor regardless.
                DestinationMode = RestoreDestinationMode.InPlace,
                // A fresh run identifier per invocation, so two restores of one
                // snapshot displace into distinct stores (architecture 08 §3.1).
                RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            cancellationToken).ConfigureAwait(false);

        var directories = plan.Items
            .Where(item => item.Kind == EntryKind.DirectoryPlaceholder)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);
        // A degraded file's content was written and verified — it counts as
        // written — but the shortfall is named on its own line and in the
        // summary, because the file on disk is not the file that was captured.
        var restored = receipt.Items.Count(
            item => item.Outcome is "restored" or "degraded" && !directories.Contains(item.Path));
        var failed = receipt.Items.Count(item => item.Outcome == "failed");
        var skipped = receipt.Items.Count(item => item.Outcome == "skipped");
        var degraded = receipt.Items.Count(item => item.Outcome == "degraded");

        List<string> lines = [];
        foreach (var item in receipt.Items.Where(item => item.Outcome is "failed" or "skipped" or "degraded"))
        {
            lines.Add($"{item.Outcome} {item.Path}: {item.Detail}");
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"restore {receipt.Outcome}: {restored} file(s) to {receipt.WrittenTo}; {failed} failure(s), {skipped} skipped, {degraded} degraded"));

        return new OperationReport(receipt.Outcome is RestoreOutcome.Complete, lines);
    }

    /// <inheritdoc/>
    public ValueTask<OperationReport> SyncAsync(
        string? setName, string? destinationName, CancellationToken cancellationToken) =>
        // Fan-out belongs to the service: its scheduler owns the transfer
        // lane, its ledger records the outcome, and its runtime holds every
        // set's staging archive. A direct-mode copy would race all three.
        throw new CliFailureException(Strings.DirectGateway_SyncNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> VerifyDestinationAsync(
        string? setName, string? destinationName, bool full, bool probe, CancellationToken cancellationToken) =>
        // Same reason, plus one of its own: the sweep's cursor and circuit
        // stamp live in the service's ledger, so a direct-mode pass would read
        // the same objects forever and never record that it had.
        throw new CliFailureException(Strings.DirectGateway_VerifyDestinationNeedsTheService);

    /// <inheritdoc/>
    public ValueTask<OperationReport> RetentionAsync(bool apply, CancellationToken cancellationToken) =>
        // Retention belongs to the hub for the same reason: the gate reads
        // the sync ledger, the plan reads every set's configuration, and the
        // pass runs on the writer lane a console cannot hold.
        throw new CliFailureException(Strings.DirectGateway_RetentionNeedsTheService);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    private static VerifyLevel ParseLevel(string level) => level switch
    {
        "locator" => VerifyLevel.LocatorAndFooter,
        "digest" => VerifyLevel.FooterAndDigest,
        "records" => VerifyLevel.EveryRecord,
        _ => throw new CliFailureException(Strings.FormatDirectGateway_NotVerifyLevelLocatorDigest(level)),
    };

    private static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();
}
