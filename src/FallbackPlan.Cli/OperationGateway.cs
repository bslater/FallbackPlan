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
                    return new ServiceGateway(client, address);
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
    public ValueTask DisposeAsync() => client.DisposeAsync();

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

        using var catalogue = Catalogue.Open(session.CataloguePath, session.Repository.RepositoryId);
        var snapshotId = Convert.FromHexString(request.SnapshotId);

        using var reader = new RepositoryReader(session.Repository.RepositoryId, session.Repository.Keys, session.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
        var engine = new RestoreEngine(reader);

        var restored = 0;
        var failed = 0;
        List<string> lines = [];

        async ValueTask RestoreEntryAsync(CatalogueTreeEntry entry)
        {
            if (entry.EntryKind == EntryKind.DirectoryPlaceholder)
            {
                Directory.CreateDirectory(
                    Path.Combine(request.OutputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                foreach (var child in catalogue.ListDirectory(snapshotId, entry.Path))
                {
                    await RestoreEntryAsync(child).ConfigureAwait(false);
                }

                return;
            }

            var read = await reader.ReadSegmentAsync(entry.ObjectId, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                lines.Add($"FAILED {entry.Path}: manifest read {read.Outcome}");
                failed++;
                return;
            }

            var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
            if (manifest.EntryKind != EntryKind.File)
            {
                // Symlinks and specials materialise in wave R's planner;
                // reported, never silently dropped.
                lines.Add($"skipped {entry.Path}: {manifest.EntryKind} restore lands with the restore planner");
                return;
            }

            var destinationPath = Path.Combine(
                request.OutputDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            Repository.RestoreResult result;
            var destination = File.Create(destinationPath);
            await using (destination.ConfigureAwait(false))
            {
                result = await engine.RestoreFileAsync(manifest, destination, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                File.Delete(destinationPath);
                lines.Add($"FAILED {entry.Path}: {result.FailureDetail}");
                failed++;
                return;
            }

            restored++;
        }

        if (request.Path is { Length: > 0 } wanted)
        {
            var entry = catalogue.LookupPath(snapshotId, wanted)
                ?? throw new CliFailureException(Strings.FormatCliApplication_DoesNotExistSnapshot(wanted, request.SnapshotId));
            await RestoreEntryAsync(entry).ConfigureAwait(false);
        }
        else
        {
            var roots = catalogue.ListDirectory(snapshotId, string.Empty);
            if (roots.Count == 0)
            {
                throw new CliFailureException(Strings.FormatDirectGateway_CatalogueKnowsNothingUnderSnapshot(request.SnapshotId));
            }

            foreach (var entry in roots)
            {
                await RestoreEntryAsync(entry).ConfigureAwait(false);
            }
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"restored {restored} file(s) to {request.OutputDirectory}; {failed} failure(s)"));

        return new OperationReport(failed == 0, lines);
    }

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
