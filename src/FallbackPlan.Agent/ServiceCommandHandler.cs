using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Agent;

/// <summary>
/// The service side of the command contract (ADR-0028 §7).
/// </summary>
/// <remarks>
/// <para>
/// Every expected failure is a <see cref="ServiceError"/> rather than an
/// exception (NFR-PORT-004): an exception thrown here would cross a process
/// boundary, lose its type, and arrive as a string the client has to parse.
/// </para>
/// <para>
/// Key material never appears in any command or result, in either direction
/// (NFR-SEC-009). Exporting a recovery kit is <b>not a command</b> — it
/// re-derives the key-encryption key from a passphrase supplied per invocation,
/// so it runs where the person typed it.
/// </para>
/// </remarks>
public sealed class ServiceCommandHandler(ServiceRuntime runtime, RemoteBindingState remoteBinding)
    : IFallbackPlanService
{
    /// <inheritdoc/>
    public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ValueTask.FromResult(Dispatch(command));
        }
        catch (ClientStateException exception)
        {
            return ValueTask.FromResult<ServiceResult>(
                new ServiceError(ServiceErrorReason.Failed, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<ServiceResult>(
                new ServiceError(ServiceErrorReason.Failed, exception.Message));
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
        runtime.Progress.WatchAsync(cancellationToken);

    private ServiceResult Dispatch(ServiceCommand command) => command switch
    {
        ListBackupSetsCommand => ListBackupSets(),
        UpsertBackupSetCommand upsert => UpsertBackupSet(upsert),
        RunBackupCommand run => RunBackup(run),
        CancelJobCommand cancel => CancelJob(cancel),
        ListJobsCommand list => ListJobs(list),
        ListSnapshotsCommand => ListSnapshots(),
        ListDirectoryCommand list => ListDirectory(list),
        GetStatusCommand => GetStatus(),
        ExportConfigurationCommand => new ConfigurationResult(runtime.Configuration.ExportJson()),
        DescribeServiceCommand => Describe(),

        // Named rather than silently missing: the contract carries these so a
        // client can be written against them, and this build serves the writer
        // path — where the multi-process hazard lived — while restore, verify
        // and check remain read paths a client runs in direct mode.
        PlanRestoreCommand or RunRestoreCommand => NotServed("restore"),
        VerifyCommand => NotServed("verify"),
        CheckCommand => NotServed("check"),

        _ => new ServiceError(ServiceErrorReason.InvalidArgument, $"Unknown command '{command.GetType().Name}'."),
    };

    private static ServiceError NotServed(string what) =>
        new ServiceError(
            ServiceErrorReason.Unavailable,
            $"This service build does not serve '{what}' over the command surface. It is a read path: run it "
            + "against the repository directly. The contract carries the command so a client written today keeps "
            + "working when the service does serve it.");

    private BackupSetsResult ListBackupSets() =>
        new BackupSetsResult(
            [.. runtime.Configuration.BackupSets.Select(set => new BackupSetDescriptor(
                set.Id, set.Name, set.Root, set.Schedule, set.IncludeRules, set.ExcludeRules))]);

    private ServiceResult UpsertBackupSet(UpsertBackupSetCommand command)
    {
        var configuration = runtime.Configuration;
        var replacement = new BackupSetConfiguration
        {
            Id = command.Set.Id,
            Name = command.Set.Name,
            Root = command.Set.Root,
            Schedule = command.Set.Schedule,
            IncludeRules = command.Set.IncludeRules,
            ExcludeRules = command.Set.ExcludeRules,
        };

        var sets = configuration.BackupSets
            .Where(set => !string.Equals(set.Id, replacement.Id, StringComparison.Ordinal))
            .Append(replacement)
            .ToList();

        try
        {
            // Save validates: an invalid set is refused here rather than
            // discovered by the scheduler at two in the morning.
            new ClientConfiguration
            {
                SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
                BackupSets = sets,
            }.Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        return new AcknowledgedResult();
    }

    private ServiceResult RunBackup(RunBackupCommand command)
    {
        var configuration = runtime.Configuration;
        var set = command.SetName is null
            ? configuration.BackupSets.Count > 0 ? configuration.BackupSets[0] : null
            : configuration.FindSet(command.SetName);

        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                command.SetName is null
                    ? "No backup set is configured."
                    : $"No backup set named '{command.SetName}' is configured.");
        }

        // A user-initiated run outranks scheduled work already waiting
        // (ADR-0029 §4). The result is the job identity, not the backup — a
        // client watches progress rather than holding a connection open for
        // hours.
        var job = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true);
        _ = job.ContinueWith(static _ => { }, TaskScheduler.Default);

        return new JobAcceptedResult(Scheduler.LatestJobFor(runtime, set.Id) ?? string.Empty);
    }

    private ServiceResult CancelJob(CancelJobCommand command) =>
        runtime.Queue.Cancel(command.JobId)
            ? new AcknowledgedResult()
            : new ServiceError(
                ServiceErrorReason.NotFound,
                $"No job '{command.JobId}' is queued or running. A finished job cannot be cancelled.");

    private JobsResult ListJobs(ListJobsCommand command)
    {
        var jobs = runtime.Jobs.Jobs.AsEnumerable();
        if (command.ActiveOnly)
        {
            jobs = jobs.Where(job => runtime.Queue.IsActive(job.Id));
        }

        return new JobsResult(
            [.. jobs.Select(job => new JobDescriptor(
                job.Id, job.BackupSetId, job.State, job.StartedAt, job.UpdatedAt, job.SnapshotId, job.Detail))]);
    }

    private SnapshotsResult ListSnapshots()
    {
        using var catalogue = runtime.OpenReadCatalogue();
        return new SnapshotsResult(
            [.. catalogue.EnumerateSnapshots().Select(row => new SnapshotDescriptor(
                Convert.ToHexString(row.SnapshotId.Span).ToLowerInvariant(),
                Convert.ToHexString(row.BackupSetId.Span).ToLowerInvariant(),
                row.CapturedAt,
                row.CaptureStatus,
                0))]);
    }

    private ServiceResult ListDirectory(ListDirectoryCommand command)
    {
        byte[] snapshotId;
        try
        {
            snapshotId = Convert.FromHexString(command.SnapshotId);
        }
        catch (FormatException)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, $"'{command.SnapshotId}' is not a hex snapshot identifier.");
        }

        using var catalogue = runtime.OpenReadCatalogue();
        var known = catalogue.EnumerateSnapshots()
            .Any(row => row.SnapshotId.Span.SequenceEqual(snapshotId));
        if (!known)
        {
            return new ServiceError(ServiceErrorReason.NotFound, $"No snapshot '{command.SnapshotId}' exists.");
        }

        var path = command.Path ?? string.Empty;
        var entries = catalogue.ListDirectory(snapshotId, path);
        return new DirectoryResult(
            path,
            [.. entries.Select(entry => new DirectoryEntryDescriptor(
                entry.Path.Split('/')[^1],
                entry.EntryKind.ToString().ToLowerInvariant(),
                (long)(entry.LogicalLength ?? 0)))]);
    }

    private StatusResult GetStatus()
    {
        using var catalogue = runtime.OpenReadCatalogue();
        var configuration = runtime.Configuration;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = catalogue.EnumerateSnapshots().ToList();
        var findings = catalogue.Findings().Count;
        var sets = new List<BackupSetStatusDescriptor>();

        foreach (var set in configuration.BackupSets)
        {
            var setId = Convert.FromHexString(set.Id);
            var latest = snapshots.LastOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));

            var status = StatusDeriver.Derive(new StatusInputs
            {
                LatestSnapshotAt = latest?.CapturedAt,
                LatestCaptureStatus = latest?.CaptureStatus,
                DestinationReachable = Directory.Exists(runtime.Options.RepositoryPath),
                SameFailureDomain = true,
                DamageFindings = findings,
                RequiredObjectsMissing = false,
            });

            string? nextRun = null;
            if (!string.IsNullOrWhiteSpace(set.Schedule) && Schedule.TryParse(set.Schedule, out var schedule, out _))
            {
                var anchor = runtime.Jobs.LastCompleted(set.Id) is { } completed
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)completed.UpdatedAt)
                    : (DateTimeOffset?)null;
                nextRun = schedule!.NextRun(anchor, DateTimeOffset.Now).ToString("u");
            }

            sets.Add(new BackupSetStatusDescriptor(set.Name, status, nextRun));
        }

        return new StatusResult(Environment.MachineName, sets, now);
    }

    private ServiceDescriptionResult Describe() =>
        new ServiceDescriptionResult(
            ContractVersion.Current.ToString(),
            "fallbackplan-agent/0.1",
            Environment.MachineName,
            runtime.Options.StateDirectory,
            remoteBinding.Enabled,
            runtime.Queue.ActiveCount);
}

/// <summary>Whether this service's remote binding is on, and why not when it is not.</summary>
/// <param name="Enabled">Whether the remote binding is listening.</param>
/// <param name="Reason">Why it is not, when it is not.</param>
public sealed record RemoteBindingState(bool Enabled, string? Reason)
{
    /// <summary>The state of a default install: no port, nothing listening.</summary>
    public static RemoteBindingState Off { get; } = new(false, null);
}
