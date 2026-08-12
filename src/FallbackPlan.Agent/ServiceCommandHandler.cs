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
                PlanRestoreCommand plan => await PlanRestoreAsync(plan, cancellationToken).ConfigureAwait(false),
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
                RetentionCommand retention => await OnWriterLaneAsync(
                    retention.Apply ? "retention apply" : "retention plan",
                    token => RetentionAsync(retention, token),
                    cancellationToken).ConfigureAwait(false),
                _ => await DispatchAsync(command, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// Runs a pass on the queue's writer lane and waits for it. Retention is
    /// a writer: it tombstones and deletes in the staging archives, so it
    /// serialises against backups rather than racing them (ADR-0029 §4's
    /// reasoning, applied to the one maintenance path that mutates).
    /// </summary>
    private async ValueTask<ServiceResult> OnWriterLaneAsync(
        string description,
        Func<CancellationToken, ValueTask<ServiceResult>> work,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = $"write-{Guid.NewGuid():n}";

        runtime.Queue.Enqueue(new QueuedJob(
            jobId,
            JobLane.Writer,
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

        using var registration = cancellationToken.Register(() => runtime.Queue.Cancel(jobId));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// One retention pass per configured set with a staging archive
    /// (architecture 07): report always, tombstone and sweep only on apply.
    /// A gate hold past its deferral bound raises the FR-GC-009 warning as a
    /// durable notice.
    /// </summary>
    private async ValueTask<ServiceResult> RetentionAsync(RetentionCommand command, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var (set, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            var report = await Retention.RetentionRunner.RunAsync(
                archive.Store,
                archive.Repository,
                set.Retention,
                set.Destinations,
                name => runtime.DestinationSync.Find(set.Id, name),
                runtime.Writer,
                command.Apply,
                now,
                cancellationToken).ConfigureAwait(false);

            lines.AddRange(report.Lines.Select(line => $"{set.Name}: {line}"));

            foreach (var held in report.Held.Where(candidate => candidate.DeferralExceeded))
            {
                foreach (var laggard in held.AwaitingDestinations)
                {
                    runtime.Notices.Raise(
                        $"retention-deferred:{set.Id}:{laggard}",
                        $"Set '{set.Name}' holds expired history because destination '{laggard}' has not "
                        + "received it for longer than the deferral bound — reconnect the destination or "
                        + "remove it, or the staging archive keeps growing (FR-GC-009).",
                        now);
                }
            }
        }

        return new RetentionResult(lines);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
        runtime.Progress.WatchAsync(cancellationToken);

    private async ValueTask<ServiceResult> DispatchAsync(ServiceCommand command, CancellationToken cancellationToken) => command switch
    {
        ListBackupSetsCommand => ListBackupSets(),
        UpsertBackupSetCommand upsert => UpsertBackupSet(upsert),
        RunBackupCommand run => RunBackup(run),
        CancelJobCommand cancel => CancelJob(cancel),
        ListJobsCommand list => ListJobs(list),
        ListSnapshotsCommand => await ListSnapshotsAsync(cancellationToken).ConfigureAwait(false),
        ListDirectoryCommand list => await ListDirectoryAsync(list, cancellationToken).ConfigureAwait(false),
        GetStatusCommand => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
        ExportConfigurationCommand => new ConfigurationResult(runtime.Configuration.ExportJson()),
        DescribeServiceCommand => Describe(),

        // The read paths are handled before this dispatch, on the reader lane.
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

    /// <summary>
    /// The archive holding a snapshot, found by asking each existing set
    /// archive's catalogue. Null when no archive knows it.
    /// </summary>
    private async ValueTask<ArchiveHandle?> FindArchiveBySnapshotAsync(
        byte[] snapshotId, CancellationToken cancellationToken)
    {
        foreach (var (_, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var catalogue = archive.OpenReadCatalogue();
            if (catalogue.EnumerateSnapshots().Any(row => row.SnapshotId.Span.SequenceEqual(snapshotId)))
            {
                return archive;
            }
        }

        return null;
    }

    /// <summary>Plans a restore without performing it — a catalogue walk plus one store probe per located blob.</summary>
    private async ValueTask<ServiceResult> PlanRestoreAsync(PlanRestoreCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        var archive = await FindArchiveBySnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No set's archive holds snapshot {command.SnapshotId}.");
        }

        using var catalogue = archive.OpenReadCatalogue();
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
        // before any byte moves rather than discovered part-way through. The
        // catalogue alone cannot answer this — it is a cache, and a cache
        // ahead of the store says "nothing missing" about the very objects
        // the store has lost — so each located blob is probed against the
        // store, one memoized metadata call per distinct blob. What the
        // probe cannot see is a lost blob holding only the items' SEGMENTS
        // (the plan carries manifests, not their references); naming those
        // too is FR-RST-003's completeness work.
        using var keyDeriver = new Repository.Crypto.StoreBlobKeyDeriver(archive.Repository.Keys.KeyIdKey);
        var blobPresent = new Dictionary<Domain.Identifiers.BlobId, bool>();
        var missing = new List<string>();

        foreach (var item in plan.Items)
        {
            if (item.Kind == EntryKind.DirectoryPlaceholder)
            {
                continue;
            }

            if (catalogue.ResolveLocation(item.ObjectId) is not { } location)
            {
                missing.Add(item.Path);
                continue;
            }

            if (!blobPresent.TryGetValue(location.BlobId, out var present))
            {
                var blobKey = location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId);
                present =
                    await BlobExistsAsync(archive, BlobClass.Metadata, blobKey, cancellationToken).ConfigureAwait(false)
                    || await BlobExistsAsync(archive, BlobClass.Data, blobKey, cancellationToken).ConfigureAwait(false);
                blobPresent[location.BlobId] = present;
            }

            if (!present)
            {
                missing.Add(item.Path);
            }
        }

        return new RestorePlanResult(
            plan.Items.Count(item => item.Kind != EntryKind.DirectoryPlaceholder),
            (long)plan.SpaceEstimateBytes,
            missing);
    }

    private static async ValueTask<bool> BlobExistsAsync(
        ArchiveHandle archive, BlobClass blobClass, Domain.Identifiers.StoreBlobKey blobKey, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await archive.Store.GetMetadataAsync(
                Repository.Packing.BlobStoreKeys.ForBlob(blobClass, blobKey), cancellationToken).ConfigureAwait(false);
            return metadata.Found && metadata.Metadata!.Length > 0;
        }
        catch (IOException)
        {
            // A store that cannot answer is reported as missing: the plan's
            // job is to warn before bytes move, and "unreachable" warrants
            // the warning as much as "absent".
            return false;
        }
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

        var archive = await FindArchiveBySnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No set's archive holds snapshot {command.SnapshotId}.");
        }

        using var catalogue = archive.OpenReadCatalogue();
        var plan = RestorePlanner.Plan(catalogue, snapshotId, command.Path ?? string.Empty, target);
        if (plan.Items.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"The catalogue knows nothing under snapshot {command.SnapshotId}.");
        }

        using var reader = new RepositoryReader(archive.Repository.RepositoryId, archive.Repository.Keys, archive.Store);
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
            // A degraded file's content was written and verified, so it counts
            // among the files written; the Outcome below carries the shortfall
            // (Partial), exactly as it carries a skipped symlink's.
            receipt.Items.Count(item => item.Outcome is "restored" or "degraded" && !directories.Contains(item.Path)),
            receipt.Items.Count(item => item.Outcome == "failed"),
            // Where the files actually are, not where the caller pointed.
            // Historical content quarantines by default (FR-RST-006), so the
            // two differ, and a caller told the wrong one cannot find its data.
            receipt.WrittenTo,
            // The outcome the executor computed — carried whole so a Partial
            // restore is not reported to a remote client as success (FR-RST-005).
            receipt.Outcome.ToString().ToLowerInvariant());
    }

    /// <summary>Verifies every stored blob of every set's archive at the requested level.</summary>
    private async ValueTask<ServiceResult> VerifyAsync(VerifyCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseLevel(command.Level, out var level, out var canonical, out var invalid))
        {
            return invalid!;
        }

        var examined = 0L;
        var failures = 0L;

        foreach (var (_, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var verifier = new VerifyEngine(archive.Repository.RepositoryId, archive.Repository.Keys, archive.Store);
            await foreach (var entry in archive.Store
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
        }

        return new VerificationResult(examined, failures, canonical);
    }

    /// <summary>Health across every set's archive: the blob sweep, the journal survey, and the catalogue's damage findings.</summary>
    private async ValueTask<ServiceResult> CheckAsync(CheckCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseLevel(command.Level, out var level, out _, out var invalid))
        {
            return invalid!;
        }

        // Findings only. A count of what was healthy is not a finding, and the
        // contract says this list is "the findings, in the order they matter" —
        // so an empty list is the answer "nothing is wrong", not "nothing ran".
        // Findings name their set, because "which archive" is the first
        // question a finding raises when there are several.
        var findings = new List<string>();

        foreach (var (set, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            using (var verifier = new VerifyEngine(archive.Repository.RepositoryId, archive.Repository.Keys, archive.Store))
            {
                await foreach (var entry in archive.Store
                    .ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var result = await verifier.VerifyBlobAsync(entry.Key, entry.Length, level, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        findings.Add($"{set.Name}: blob {entry.Key.Value}: {result.Detail}");
                    }
                }
            }

            using (var journalReader = new JournalReader(
                archive.Store, archive.Repository.RepositoryId, archive.Repository.Hierarchy))
            {
                var generation = archive.Repository.CurrentDataGeneration.Value >= archive.Repository.CurrentMetadataGeneration.Value
                    ? archive.Repository.CurrentDataGeneration.Value
                    : archive.Repository.CurrentMetadataGeneration.Value;

                var (_, unparseable, journalFindings) = await journalReader
                    .LoadAsync(generation, cancellationToken).ConfigureAwait(false);

                if (unparseable > 0)
                {
                    findings.Add(string.Create(
                        CultureInfo.InvariantCulture, $"{set.Name}: journal: {unparseable} unparseable record(s)"));
                }

                findings.AddRange(journalFindings.Select(finding => $"{set.Name}: journal {finding.Kind}: {finding.Detail}"));
            }

            using (var catalogue = archive.OpenReadCatalogue())
            {
                findings.AddRange(catalogue.Findings().Select(finding => $"{set.Name}: catalogue {finding.Kind}: {finding.Detail}"));
            }
        }

        return new CheckResult(findings);
    }

    private BackupSetsResult ListBackupSets() =>
        new BackupSetsResult(
            [.. runtime.Configuration.BackupSets.Select(set => new BackupSetDescriptor(
                set.Id, set.Name, set.Root, set.Schedule, set.IncludeRules, set.ExcludeRules,
                [.. set.Destinations.Select(reference => reference.Ref)]))]);

    private ServiceResult UpsertBackupSet(UpsertBackupSetCommand command)
    {
        var configuration = runtime.Configuration;

        // The command names destinations; a retention override stays a
        // configuration-file concern until a client needs to write one.
        // An upsert keeps any override the set already carried per name.
        var existing = configuration.BackupSets
            .FirstOrDefault(set => string.Equals(set.Id, command.Set.Id, StringComparison.Ordinal));
        var replacement = new BackupSetConfiguration
        {
            Id = command.Set.Id,
            Name = command.Set.Name,
            Root = command.Set.Root,
            Schedule = command.Set.Schedule,
            IncludeRules = command.Set.IncludeRules,
            ExcludeRules = command.Set.ExcludeRules,
            Retention = existing?.Retention,
            Destinations = [.. command.Set.Destinations.Select(name =>
                existing?.Destinations.FirstOrDefault(reference =>
                    string.Equals(reference.Ref, name, StringComparison.Ordinal))
                ?? new SetDestinationReference { Ref = name })],
        };

        var sets = configuration.BackupSets
            .Where(set => !string.Equals(set.Id, replacement.Id, StringComparison.Ordinal))
            .Append(replacement)
            .ToList();

        try
        {
            // Save validates: an invalid set — including one referencing no
            // declared destination (FR-DEST-001) — is refused here rather
            // than discovered by the scheduler at two in the morning.
            (configuration with { BackupSets = sets }).Save(runtime.ConfigurationPath);
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
        var now = DateTimeOffset.Now;
        var job = Scheduler.Enqueue(runtime, set, now, userInitiated: true);

        // A committed snapshot starts its fan-out promptly rather than waiting
        // for the next pass (ADR-0034 §3); the pass still catches up anything
        // this misses, so this is responsiveness, never correctness.
        _ = job.ContinueWith(
            completed =>
            {
                if (completed is { Status: TaskStatus.RanToCompletion, Result.Outcome: "ran" })
                {
                    FanOut.EnqueueAll(runtime, set, now, userInitiated: true);
                }
            },
            TaskScheduler.Default);

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

    private async ValueTask<ServiceResult> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<SnapshotDescriptor>();
        foreach (var (_, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var catalogue = archive.OpenReadCatalogue();
            snapshots.AddRange(catalogue.EnumerateSnapshots().Select(row => new SnapshotDescriptor(
                Convert.ToHexString(row.SnapshotId.Span).ToLowerInvariant(),
                Convert.ToHexString(row.BackupSetId.Span).ToLowerInvariant(),
                row.CapturedAt,
                row.CaptureStatus,
                0)));
        }

        return new SnapshotsResult(snapshots);
    }

    private async ValueTask<ServiceResult> ListDirectoryAsync(ListDirectoryCommand command, CancellationToken cancellationToken)
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

        var archive = await FindArchiveBySnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return new ServiceError(ServiceErrorReason.NotFound, $"No snapshot '{command.SnapshotId}' exists.");
        }

        using var catalogue = archive.OpenReadCatalogue();
        var path = command.Path ?? string.Empty;
        var entries = catalogue.ListDirectory(snapshotId, path);
        return new DirectoryResult(
            path,
            [.. entries.Select(entry => new DirectoryEntryDescriptor(
                entry.Path.Split('/')[^1],
                entry.EntryKind.ToString().ToLowerInvariant(),
                (long)(entry.LogicalLength ?? 0)))]);
    }

    private async ValueTask<ServiceResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sets = new List<BackupSetStatusDescriptor>();

        foreach (var set in configuration.BackupSets)
        {
            // Each set answers from its own archive (ADR-0034). A set never
            // backed up has no archive: no snapshot, no findings — Unprotected
            // by honest absence rather than by error.
            Repository.Catalogue.CatalogueSnapshot? latest = null;
            var findings = 0;
            if (await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is { } archive)
            {
                using var catalogue = archive.OpenReadCatalogue();
                var setId = Convert.FromHexString(set.Id);
                latest = catalogue.EnumerateSnapshots()
                    .LastOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));
                findings = catalogue.Findings().Count;
            }

            var (inputs, rows) = DescribeDestinations(configuration, set);
            var status = StatusDeriver.Derive(new StatusInputs
            {
                LatestSnapshotAt = latest?.CapturedAt,
                LatestCaptureStatus = latest?.CaptureStatus,
                Destinations = inputs,
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

            sets.Add(new BackupSetStatusDescriptor(set.Name, status, nextRun, rows));
        }

        return new StatusResult(
            Environment.MachineName, sets, now,
            [.. runtime.Notices.Unacknowledged.Select(notice => $"[{notice.Id}] {notice.Message}")]);
    }

    /// <summary>
    /// One set's destination matrix, twice over: the derivation's inputs and
    /// the client's rows, built together so they cannot disagree.
    /// </summary>
    private (IReadOnlyList<DestinationStatusInput> Inputs, IReadOnlyList<DestinationStatusDescriptor> Rows)
        DescribeDestinations(ClientConfiguration configuration, BackupSetConfiguration set)
    {
        var lastCompleted = runtime.Jobs.LastCompleted(set.Id)?.UpdatedAt ?? 0;
        var inputs = new List<DestinationStatusInput>();
        var rows = new List<DestinationStatusDescriptor>();

        foreach (var reference in set.Destinations)
        {
            var destination = configuration.FindDestination(reference.Ref);
            if (destination is null)
            {
                // Validation refuses dangling references, so this is a config
                // edited mid-flight — reported, never invented around.
                inputs.Add(new DestinationStatusInput
                {
                    Name = reference.Ref,
                    Kind = DestinationKind.LocalPath,
                    Sync = DestinationSyncState.Failed,
                    SameFailureDomain = false,
                    Detail = "no longer declared",
                });
                rows.Add(new DestinationStatusDescriptor(reference.Ref, "?", "failed", null, "no longer declared"));
                continue;
            }

            var record = runtime.DestinationSync.Find(set.Id, reference.Ref);
            var sync = record?.State ?? DestinationSyncState.Behind;
            if (sync == DestinationSyncState.InSync && (record!.LastSuccessAt ?? 0) < lastCompleted)
            {
                // In sync as of an older snapshot: the staging archive moved on.
                sync = DestinationSyncState.Behind;
            }

            inputs.Add(new DestinationStatusInput
            {
                Name = destination.Name,
                Kind = destination.Kind,
                Sync = sync,
                SameFailureDomain = SharesSourceFailureDomain(set, destination),
                LastSuccessAt = record?.LastSuccessAt,
                Detail = record?.LastError,
            });
            rows.Add(new DestinationStatusDescriptor(
                destination.Name, KindLabel(destination.Kind), StateLabel(sync), record?.LastSuccessAt, record?.LastError));
        }

        return (inputs, rows);
    }

    /// <summary>
    /// Whether a destination demonstrably shares the source's failure domain.
    /// A local path is compared by device identity — the real comparison that
    /// replaces the placeholder (ADR-0018 Amendment 1) — staying conservative
    /// (same) when the platform cannot say. A peer or cloud destination is
    /// another machine by construction.
    /// </summary>
    private static bool SharesSourceFailureDomain(BackupSetConfiguration set, DestinationConfiguration destination)
    {
        if (destination.Kind != DestinationKind.LocalPath)
        {
            return false;
        }

        return !(set.Root.Length > 0
            && Filesystem.Local.LocalFileSystemSource.TryStat(set.Root, out var rootStat)
            && destination.Path is { Length: > 0 }
            && Filesystem.Local.LocalFileSystemSource.TryStat(destination.Path, out var destinationStat)
            && rootStat.Device != destinationStat.Device);
    }

    private static string KindLabel(DestinationKind kind) => kind switch
    {
        DestinationKind.LocalPath => "local-path",
        DestinationKind.Peer => "peer",
        DestinationKind.S3 => "s3",
        DestinationKind.AzureBlob => "azure-blob",
        _ => "dropbox",
    };

    private static string StateLabel(DestinationSyncState state) => state switch
    {
        DestinationSyncState.InSync => "in-sync",
        DestinationSyncState.Behind => "behind",
        DestinationSyncState.Unavailable => "unavailable",
        DestinationSyncState.Failed => "failed",
        _ => "not-supported",
    };

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
/// <param name="Reason">Where it is listening when on, or why not when off.</param>
public sealed record RemoteBindingState(bool Enabled, string? Reason)
{
    /// <summary>The state of a default install: no port, nothing listening.</summary>
    public static RemoteBindingState Off { get; } = new(false, null);

    /// <summary>The state of a service whose remote binding is listening at <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">The interface and port the binding is on.</param>
    /// <returns>The enabled state, naming where it listens.</returns>
    public static RemoteBindingState On(string endpoint) => new(true, endpoint);
}
