using Bodu;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Status;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using RestoreResult = FallbackPlan.Api.RestoreResult;

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
    public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // The read paths are the only commands that do open-ended work, so
            // they are the only ones that go through the queue. Everything else
            // answers from state already in hand and would gain nothing but a
            // hand-off from being scheduled.
            return command switch
            {
                PlanRestoreCommand plan => PlanRestore(plan),
                RunRestoreCommand restore => await OnReaderLaneAsync(
                    $"restore {restore.SnapshotId}",
                    token => RunRestoreAsync(restore, token),
                    cancellationToken).ConfigureAwait(false),
                VerifyCommand verify => await OnReaderLaneAsync(
                    $"verify {verify.Level}",
                    token => VerifyAsync(verify, token),
                    cancellationToken).ConfigureAwait(false),
                CheckCommand check => await OnReaderLaneAsync(
                    $"check {check.Level}",
                    token => CheckAsync(check, token),
                    cancellationToken).ConfigureAwait(false),
                _ => Dispatch(command),
            };
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.Failed, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ServiceError(ServiceErrorReason.Failed, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return new ServiceError(ServiceErrorReason.Cancelled, "The operation was cancelled.");
        }
    }

    /// <summary>Runs a read path on the queue's reader lane and waits for it.</summary>
    /// <remarks>
    /// <para>
    /// ADR-0029 §4: restore and verification are separately queued and may run
    /// alongside a backup, because someone waiting on a restore must not wait
    /// for a scheduled backup and a read path never takes the writer role. The
    /// reader lane has one worker, so two heavy reads serialise against each
    /// other rather than competing for the same disk — which is what that lane
    /// was built for and has until now had nothing to carry.
    /// </para>
    /// <para>
    /// No job-journal entry is written. The journal is keyed by backup set, and
    /// a restore has no set; inventing one would put a synthetic identity in the
    /// same table the scheduler reads back as "last completed" for a real set.
    /// The consequence is that these jobs are not reachable by
    /// <see cref="CancelJobCommand"/> — the commands return no job id to cancel
    /// — so cancellation rides the caller's token instead, which is the reach a
    /// synchronous result gives it.
    /// </para>
    /// </remarks>
    private async ValueTask<ServiceResult> OnReaderLaneAsync(
        string description,
        Func<CancellationToken, ValueTask<ServiceResult>> work,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = $"read-{Guid.NewGuid():n}";

        runtime.Queue.Enqueue(new QueuedJob(
            jobId,
            JobLane.Reader,
            UserInitiated: true,
            description,
            async token =>
            {
                try
                {
                    completion.SetResult(await work(token).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }));

        // A caller that gives up releases the lane rather than leaving it held
        // by work nobody is waiting for.
        using var registration = cancellationToken.Register(() => runtime.Queue.Cancel(jobId));
        return await completion.Task.ConfigureAwait(false);
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

        // The read paths are handled before Dispatch, on the reader lane.
        _ => new ServiceError(ServiceErrorReason.InvalidArgument, $"Unknown command '{command.GetType().Name}'."),
    };

    /// <summary>Parses a verify level, or says what the vocabulary is.</summary>
    private static bool TryParseLevel(string text, out VerifyLevel level, out string canonical, out ServiceError? error)
    {
        (level, canonical, error) = text switch
        {
            "locator" => (VerifyLevel.LocatorAndFooter, "locator", (ServiceError?)null),
            "digest" => (VerifyLevel.FooterAndDigest, "digest", null),
            "records" => (VerifyLevel.EveryRecord, "records", null),
            _ => (default, string.Empty, new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{text}' is not a verify level (locator | digest | records).")),
        };

        return error is null;
    }

    /// <summary>Parses a hex snapshot id, or says why it is not one.</summary>
    private static bool TryParseSnapshotId(string text, out byte[] snapshotId, out ServiceError? error)
    {
        try
        {
            snapshotId = Convert.FromHexString(text);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            snapshotId = [];
            error = new ServiceError(ServiceErrorReason.InvalidArgument, $"'{text}' is not a hex snapshot identifier.");
            return false;
        }
    }

    /// <summary>Plans a restore without performing it — a catalogue walk, so it answers inline.</summary>
    private ServiceResult PlanRestore(PlanRestoreCommand command)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        using var catalogue = runtime.OpenReadCatalogue();
        var plan = RestorePlanner.Plan(
            catalogue, snapshotId, command.Path ?? string.Empty, RestoreTargetProfile.ForLocalPlatform());

        if (plan.Items.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"The catalogue knows nothing under snapshot {command.SnapshotId}"
                + $"{(command.Path is { Length: > 0 } path ? $" at '{path}'" : string.Empty)}.");
        }

        // What a plan is for: the objects it needs and cannot find, reported
        // before any byte moves rather than discovered part-way through.
        var missing = plan.Items
            .Where(item => item.Kind != EntryKind.DirectoryPlaceholder && !catalogue.HasLocation(item.ObjectId))
            .Select(item => item.Path)
            .ToList();

        return new RestorePlanResult(
            plan.Items.Count(item => item.Kind != EntryKind.DirectoryPlaceholder),
            (long)plan.SpaceEstimateBytes,
            missing);
    }

    /// <summary>Performs a restore, writing on this machine (ADR-0028 §6).</summary>
    private async ValueTask<ServiceResult> RunRestoreAsync(RunRestoreCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        if (string.IsNullOrWhiteSpace(command.OutputDirectory))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "A restore needs an output directory.");
        }

        var target = RestoreTargetProfile.ForLocalPlatform();

        using var catalogue = runtime.OpenReadCatalogue();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, command.Path ?? string.Empty, target);
        if (plan.Items.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"The catalogue knows nothing under snapshot {command.SnapshotId}.");
        }

        using var reader = new RepositoryReader(runtime.Repository.RepositoryId, runtime.Repository.Keys, runtime.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        // The output directory is a path on the machine running the service:
        // a restore commanded from elsewhere writes here and the caller is told
        // what happened, never sent the files (ADR-0028 §6).
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan,
            command.OutputDirectory,
            new RestoreExecutionOptions
            {
                // A fresh identifier per run, not per snapshot: two restores of
                // one snapshot must displace into distinct stores, or the second
                // overwrites the first's displaced copies — the single shared
                // refuge architecture 08 §3.1 forbids. A snapshot-derived id
                // made every restore of a snapshot share one.
                RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            cancellationToken).ConfigureAwait(false);

        // Files, not entries. The receipt records a created directory as
        // "restored" too, but the contract documents this field as files
        // written — and PlanRestore counts the same way, so a caller comparing
        // the plan against the outcome is comparing like with like.
        var directories = plan.Items
            .Where(item => item.Kind == EntryKind.DirectoryPlaceholder)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.Ordinal);

        return new RestoreResult(
            receipt.Items.Count(item => item.Outcome == "restored" && !directories.Contains(item.Path)),
            receipt.Items.Count(item => item.Outcome == "failed"),
            // Where the files actually are, not where the caller pointed.
            // Historical content quarantines by default (FR-RST-006), so the
            // two differ, and a caller told the wrong one cannot find its data.
            receipt.WrittenTo,
            // The outcome the executor computed — carried whole so a Partial
            // restore is not reported to a remote client as success (FR-RST-005).
            receipt.Outcome.ToString().ToLowerInvariant());
    }

    /// <summary>Verifies every stored blob at the requested level.</summary>
    private async ValueTask<ServiceResult> VerifyAsync(VerifyCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseLevel(command.Level, out var level, out var canonical, out var invalid))
        {
            return invalid!;
        }

        using var verifier = new VerifyEngine(runtime.Repository.RepositoryId, runtime.Repository.Keys, runtime.Store);
        var examined = 0L;
        var failures = 0L;

        await foreach (var entry in runtime.Store
            .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            examined++;
            var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                failures++;
            }
        }

        return new VerificationResult(examined, failures, canonical);
    }

    /// <summary>Repository health: the blob sweep, the journal survey, and the catalogue's damage findings.</summary>
    private async ValueTask<ServiceResult> CheckAsync(CheckCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseLevel(command.Level, out var level, out _, out var invalid))
        {
            return invalid!;
        }

        // Findings only. A count of what was healthy is not a finding, and the
        // contract says this list is "the findings, in the order they matter" —
        // so an empty list is the answer "nothing is wrong", not "nothing ran".
        var findings = new List<string>();

        using (var verifier = new VerifyEngine(runtime.Repository.RepositoryId, runtime.Repository.Keys, runtime.Store))
        {
            await foreach (var entry in runtime.Store
                .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Ok)
                {
                    findings.Add($"blob {entry.Key.Value}: {result.Detail}");
                }
            }
        }

        using (var journalReader = new JournalReader(
            runtime.Store, runtime.Repository.RepositoryId, runtime.Repository.Hierarchy))
        {
            var generation = runtime.Repository.CurrentDataGeneration.Value >= runtime.Repository.CurrentMetadataGeneration.Value
                ? runtime.Repository.CurrentDataGeneration.Value
                : runtime.Repository.CurrentMetadataGeneration.Value;

            var (_, unparseable, journalFindings) = await journalReader
                .LoadAsync(generation, cancellationToken).ConfigureAwait(false);

            if (unparseable > 0)
            {
                findings.Add(string.Create(
                    CultureInfo.InvariantCulture, $"journal: {unparseable} unparseable record(s)"));
            }

            findings.AddRange(journalFindings.Select(finding => $"journal {finding.Kind}: {finding.Detail}"));
        }

        using (var catalogue = runtime.OpenReadCatalogue())
        {
            findings.AddRange(catalogue.Findings().Select(finding => $"catalogue {finding.Kind}: {finding.Detail}"));
        }

        return new CheckResult(findings);
    }

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
