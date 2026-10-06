using Bodu;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Status;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;
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
/// (NFR-SEC-009). Nothing that derives from the passphrase is a command:
/// derivation runs where the person typed it, and only sealed envelopes and
/// public parameters cross this surface.
/// </para>
/// </remarks>
public sealed partial class ServiceCommandHandler(
    ServiceRuntime runtime, RemoteBindingState remoteBinding, CallerScope scope = CallerScope.Local,
    Action? requestRestart = null)
    : IFallbackPlanService
{
    /// <summary>Where the caller on this session came from.</summary>
    /// <remarks>
    /// One handler used to serve both listeners, and
    /// <see cref="RemoteBindingState"/> says only whether the remote binding
    /// is <em>on</em> — never whether <em>this</em> caller arrived over it.
    /// Refusing a verb to a remote console needs the second fact, so the host
    /// builds one handler per listener over the same runtime. Defaulting to
    /// <see cref="CallerScope.Local"/> keeps every one-shot verb and every
    /// test that constructs a handler directly meaning what it meant before.
    /// <para>
    /// <c>requestRestart</c> is the host's recycle signal (ADR-0049): the
    /// run loop hands it in so restart_service can ask the process to tear
    /// the runtime down and start it again. Null — one-shot verbs, --once,
    /// tests — means there is no host to recycle, and the verb refuses with
    /// that reason.
    /// </para>
    /// </remarks>
    private CallerScope Scope => scope;

    /// <inheritdoc/>
    public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var answer = await ExecuteGuardedAsync(command, cancellationToken).ConfigureAwait(false);

        // One trace line per command at the seam every verb crosses, so the
        // service's log reads as a conversation. Type names only: several
        // commands carry paths, and the names are enough to follow the flow.
        var log = runtime.LoggerFor<ServiceCommandHandler>();
        if (log.IsEnabled(LogLevel.Trace))
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.CommandExecuted(log, new LogLabel(command.GetType().Name), new LogLabel(answer.GetType().Name), elapsed);
        }

        return answer;
    }

    /// <summary>Dispatch with the expected-failure guards; the timing above wraps it.</summary>
    private async ValueTask<ServiceResult> ExecuteGuardedAsync(
        ServiceCommand command, CancellationToken cancellationToken)
    {
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
                OpenRestoreSourceCommand openSource => await OnReaderLaneAsync(
                    $"open restore source {openSource.SetName}",
                    token => OpenRestoreSourceAsync(openSource, token),
                    cancellationToken).ConfigureAwait(false),
                VerifyCommand verify => await OnReaderLaneAsync(
                    $"verify {verify.Level}",
                    token => VerifyAsync(verify, token),
                    cancellationToken).ConfigureAwait(false),
                CheckCommand check => await OnReaderLaneAsync(
                    $"check {check.Level}",
                    token => CheckAsync(check, token),
                    cancellationToken).ConfigureAwait(false),
                PreviewSetChangesCommand preview => await OnReaderLaneAsync(
                    $"rescan {preview.SetName ?? "(default set)"}",
                    token => PreviewSetChangesAsync(preview, token),
                    cancellationToken).ConfigureAwait(false),
                JobChangesCommand jobChanges => await OnReaderLaneAsync(
                    $"job changes {jobChanges.JobId}",
                    token => JobChangesAsync(jobChanges, token),
                    cancellationToken).ConfigureAwait(false),
                JobFailuresCommand jobFailures => await OnReaderLaneAsync(
                    $"job failures {jobFailures.JobId}",
                    token => JobFailuresAsync(jobFailures, token),
                    cancellationToken).ConfigureAwait(false),
                RetentionCommand retention => await OnWriterLaneAsync(
                    retention.Apply ? "retention apply" : "retention plan",
                    token => RetentionAsync(retention, token),
                    cancellationToken).ConfigureAwait(false),
                DeleteSnapshotsCommand deletion => await OnWriterLaneAsync(
                    deletion.Apply ? "delete snapshots" : "plan snapshot deletion",
                    token => DeleteSnapshotsAsync(deletion, token),
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

        var accepted = runtime.Queue.Enqueue(new QueuedJob(
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

        if (!accepted)
        {
            // The only refusal a fresh identity can draw is a queue that has
            // stopped. Answering it is not optional: the completion below is
            // signalled by the job running, so a caller left waiting on work
            // the queue will never take waits for ever.
            return new ServiceError(ServiceErrorReason.Cancelled, "The operation was cancelled.");
        }

        // A caller that gives up releases the lane rather than leaving it held
        // by work nobody is waiting for.
        using var registration = cancellationToken.Register(() => runtime.Queue.Cancel(jobId));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a pass on the queue's writer lane and waits for it. Retention is
    /// a writer: it tombstones and deletes in the sets' archives, so it takes
    /// the writer lane rather than racing the captures that share it — and
    /// the per-set exclusion, not the lane (now a pool, ADR-0047), is what
    /// keeps one set's retention and its capture apart (ADR-0029 §4's
    /// reasoning, applied to the one maintenance path that mutates).
    /// </summary>
    private async ValueTask<ServiceResult> OnWriterLaneAsync(
        string description,
        Func<CancellationToken, ValueTask<ServiceResult>> work,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ServiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = $"write-{Guid.NewGuid():n}";

        var accepted = runtime.Queue.Enqueue(new QueuedJob(
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

        if (!accepted)
        {
            // The only refusal a fresh identity can draw is a queue that has
            // stopped. Answering it is not optional: the completion below is
            // signalled by the job running, so a caller left waiting on work
            // the queue will never take waits for ever.
            return new ServiceError(ServiceErrorReason.Cancelled, "The operation was cancelled.");
        }

        using var registration = cancellationToken.Register(() => runtime.Queue.Cancel(jobId));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// One retention pass per configured set with an archive on disk —
    /// staging, or a direct-ship metadata store (architecture 07, ADR-0046): report always, tombstone and sweep only on apply.
    /// A gate hold past its deferral bound raises the FR-GC-009 warning as a
    /// durable notice, and a snapshot kept because its capture time is
    /// implausible raises FR-GC-012's.
    /// </summary>
    private async ValueTask<ServiceResult> RetentionAsync(RetentionCommand command, CancellationToken cancellationToken)
    {
        var archives = await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false);

        // The run's authority to author deletions (ADR-0055 §6), opened and
        // proved for every set before any set runs, so a grant from another
        // passphrase, or derived under another set's salt, is refused before
        // the run has written anything anywhere (Amendment 3). Each is zeroed
        // when its set's run ends, so a service compromised between runs
        // holds nothing that can delete.
        var grants = new Dictionary<string, Repository.Crypto.ReclaimAuthority>(StringComparer.Ordinal);
        var ungranted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (set, archive) in archives)
            {
                var (grant, unnamed, refusal) = await OpenReclaimGrantAsync(
                    set, archive, command, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return refusal;
                }

                if (grant is not null)
                {
                    grants[set.Id] = grant;
                }

                if (unnamed)
                {
                    ungranted.Add(set.Id);
                }
            }

            return await RunRetentionAsync(command, archives, grants, ungranted, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var grant in grants.Values)
            {
                grant.Dispose();
            }
        }
    }

    /// <summary>
    /// The pass itself, set by set, under the grants already proved. A set in
    /// <paramref name="ungranted"/> was left out of the command's grants, and
    /// is reported and not applied.
    /// </summary>
    private async ValueTask<ServiceResult> RunRetentionAsync(
        RetentionCommand command,
        IReadOnlyList<(Application.BackupSetConfiguration Set, ArchiveHandle Archive)> archives,
        Dictionary<string, Repository.Crypto.ReclaimAuthority> grants,
        HashSet<string> ungranted,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clockSkewMargin = runtime.Configuration.EffectiveClockSkewMargin;

        foreach (var (set, archive) in archives)
        {
            // The set gate (ADR-0029 Amendment 2): the destructive half must
            // not run while a sync for this set is mid-flight — the two can
            // otherwise conspire to delete a trimmed blob's last copy. A sync
            // can run for hours, and the writer lane must never stall behind
            // one, so retention TRIES: unavailable means this set reports
            // only, and the deferral is named.
            SemaphoreSlim? acquiredGate = null;
            var apply = command.Apply && !ungranted.Contains(set.Id);
            if (apply)
            {
                var gate = runtime.SetGate(set.Id);
                if (gate.Wait(0, CancellationToken.None))
                {
                    acquiredGate = gate;
                }
                else
                {
                    apply = false;
                }
            }

            // Resolved once per destination per set: the verification OBJECT
            // is stable for the pass, while every probe through it still
            // asks the store afresh — which is what the execute-time
            // re-verification depends on.
            var verifications = new Dictionary<string, Retention.TrimVerification>(StringComparer.Ordinal);
            Retention.TrimVerification VerificationFor(string name)
            {
                if (!verifications.TryGetValue(name, out var verification))
                {
                    verification = TrimVerificationFor(name, archive);
                    verifications[name] = verification;
                }

                return verification;
            }

            // Held only while this set applies: a set whose sync holds the
            // gate reports, and reporting needs no authority.
            var reclaim = apply ? grants.GetValueOrDefault(set.Id) : null;

            Retention.RetentionReport report;
            try
            {
                report = await Retention.RetentionRunner.RunAsync(
                    archive.Store,
                    archive.Repository,
                    set.Retention,
                    set.Destinations,
                    name => runtime.DestinationSync.Find(set.Id, name),
                    VerificationFor,
                    runtime.Writer,
                    apply,
                    now,
                    cancellationToken,
                    set.Name,
                    runtime.LoggerFor(typeof(Retention.RetentionRunner)),
                    reclaim,
                    // Where the index now says an object lives. Without it a
                    // blob an earlier pass compacted is never condemned —
                    // its records are still reachable and still physically
                    // present — and compaction would reclaim nothing, ever
                    // (ADR-0067).
                    objectId => archive.Catalogue.ResolveLocation(objectId)?.BlobId,
                    clockSkewMargin: clockSkewMargin).ConfigureAwait(false);

                // A set's peers converge here and nowhere else (ADR-0055 §6):
                // the scheduled sync holds no authority to delete, so it
                // pushes whole copies and defers to this run, whose grant
                // signs the instruction — while the gate is held and before
                // the grant is zeroed.
                if (apply && reclaim is not null)
                {
                    lines.AddRange(
                        (await FanOut.ConvergePeersAsync(runtime, set, archive, reclaim, now, cancellationToken)
                            .ConfigureAwait(false))
                        .Select(line => $"{set.Name}: {line}"));
                }

                // And the third phase, in the same position and for the same
                // reason: it writes, so it runs while the gate is held. It
                // needs no reclaim authority, because it deletes nothing —
                // the blobs it drains are condemned by the NEXT pass's plan,
                // on the collector's own terms, and swept after their grace
                // like any other garbage (ADR-0067).
                if (apply && report.CompactionCandidates.Count > 0)
                {
                    lines.AddRange(
                        (await CompactSetAsync(runtime, set, archive, report, now, cancellationToken)
                            .ConfigureAwait(false))
                        .Select(line => $"{set.Name}: {line}"));
                }
            }
            finally
            {
                grants.GetValueOrDefault(set.Id)?.Dispose();
                acquiredGate?.Release();
            }

            lines.AddRange(report.Lines.Select(line => $"{set.Name}: {line}"));

            // A person's deletion this pass finished is one nothing should
            // list any more (FR-GC-013). An expiry's snapshot keeps its row,
            // as it always has.
            foreach (var gone in report.Swept?.DeletedSnapshots.Where(fact => fact.DeletionRequest is not null) ?? [])
            {
                archive.Catalogue.ForgetSnapshot(Convert.FromHexString(gone.SnapshotId));
            }

            if (ungranted.Contains(set.Id))
            {
                lines.Add(
                    $"{set.Name}: not applied — the run carried no reclaim grant for this set, so it was "
                    + "reported only. A set adopted under another passphrase is collected with that one.");
            }
            else if (command.Apply && !apply)
            {
                lines.Add($"{set.Name}: apply deferred — a sync for this set is in flight; re-run retention");
            }

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

            ImplausibleCaptureNotice.Report(runtime.Notices, set, report.Implausible, now);
        }

        return new RetentionResult(lines);
    }

    /// <summary>
    /// Rewrites the blobs this pass's plan chose, and moves the index onto
    /// the result ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source and destination are both <c>archive.Store</c>, which is what
    /// makes the two set shapes one path: for a staging set that is the
    /// archive's own directory, and for a direct-ship set it is the ship
    /// sink — candidates read back from the destinations in priority order,
    /// and the blobs produced written out through the same sink.
    /// </para>
    /// <para>
    /// Peers are excluded, and by the sink rather than by anything here:
    /// outside a run <c>DestinationShipSink.ReadOrder</c> resolves local
    /// paths only, because a peer shipment is a live session rather than a
    /// directory. A set whose destinations are all peers therefore lists no
    /// blobs through the sink, its plan vetoes, and there is nothing to
    /// compact — which is the same and correct answer as for a set whose
    /// local paths are simply away.
    /// </para>
    /// <para>
    /// A failure here fails the phase and not the pass. The retention the
    /// run already did is durable and correct; compaction is maintenance,
    /// and an unreadable candidate or a destination that went away mid-write
    /// is a reason to say so and try again next time, not to lose the
    /// report.
    /// </para>
    /// </remarks>
    private static async ValueTask<IReadOnlyList<string>> CompactSetAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        ArchiveHandle archive,
        Retention.RetentionReport report,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await Repository.CompactionPass.RunAsync(
                [.. report.CompactionCandidates.Select(blob =>
                    new Repository.CompactionSource(blob.StoreKey, blob.BlobId, blob.Live))],
                archive.Repository,
                archive.Store,
                archive.Store,
                runtime.Writer,
                CapturePolicy.Default,
                archive.Sequence,
                archive.Catalogue,
                archive.SpoolDirectory,
                nowUnixMilliseconds,
                declaredMaxDurationMs: (ulong)TimeSpan.FromHours(6).TotalMilliseconds,
                expiryGeneration: archive.Repository.CurrentMetadataGeneration.Value + 1,
                cancellationToken,
                logger: runtime.LoggerFor(typeof(Repository.CompactionPass))).ConfigureAwait(false);

            var reclaimable = report.CompactionCandidates
                .Where(blob => outcome.Drained.Contains(blob.BlobId))
                .Sum(blob => blob.DeadBytes);

            return
            [
                $"compacted: {outcome.RecordsMoved.ToString(CultureInfo.InvariantCulture)} record(s) out of "
                + $"{outcome.Drained.Count.ToString(CultureInfo.InvariantCulture)} blob(s) into "
                + $"{outcome.Published.Count.ToString(CultureInfo.InvariantCulture)}; "
                + $"{reclaimable.ToString("N0", CultureInfo.InvariantCulture)} byte(s) come back once the next "
                + "pass condemns them and their grace runs",
            ];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or Repository.Packing.BlobFormatException)
        {
            Log.CompactionFailed(runtime.LoggerFor(typeof(Repository.CompactionPass)), new LogLabel(set.Name), exception);
            return [$"compaction did not run: {exception.Message}"];
        }
    }

    /// <summary>
    /// How a destination's holdings can be verified for the staging trim
    /// (ADR-0034 §6): a reachable local-path replica is probed key by key —
    /// direct evidence; a peer is trusted through its sync-ledger claim
    /// <b>backed by a verification stamp</b> (FR-VER-006); anything else — an
    /// unplugged drive, an unserved kind — cannot vouch, and every blob it is
    /// entitled to stays in staging.
    /// </summary>
    /// <remarks>
    /// The declared policy is read <b>before</b> the kind. A destination
    /// excused from proving is unverifiable by its own declaration, whatever
    /// its kind — mapping it by kind would hand it a basis it can never
    /// satisfy, and the operator would see the resulting stall as a bare
    /// count with no destination named.
    /// </remarks>
    private Retention.TrimVerification TrimVerificationFor(string destinationName, ArchiveHandle archive)
    {
        var destination = runtime.Configuration.FindDestination(destinationName);
        if (destination is { RequiresVerification: false })
        {
            return Retention.TrimVerification.Declined;
        }

        switch (destination?.Kind)
        {
            case DestinationKind.LocalPath:
                var replicaRoot = Path.Combine(
                    destination.Path!, archive.Repository.RepositoryId.ToString());
                return Directory.Exists(replicaRoot)
                    ? Retention.TrimVerification.AgainstStore(
                        new Storage.Local.LocalFileSystemObjectStore(replicaRoot))
                    : Retention.TrimVerification.None;

            case DestinationKind.Peer:
                return Retention.TrimVerification.Ledger;

            default:
                return Retention.TrimVerification.None;
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
        runtime.Progress.WatchAsync(cancellationToken);

    private async ValueTask<ServiceResult> DispatchAsync(ServiceCommand command, CancellationToken cancellationToken) => command switch
    {
        ListBackupSetsCommand => ListBackupSets(),
        UpsertBackupSetCommand upsert => await UpsertBackupSetAsync(upsert, cancellationToken).ConfigureAwait(false),
        DeleteBackupSetCommand deleteSet => DeleteBackupSet(deleteSet),
        RetireStagingCommand retireStaging =>
            await RetireStagingAsync(retireStaging, cancellationToken).ConfigureAwait(false),
        UpgradeSetFormatCommand upgradeFormat =>
            await UpgradeSetFormatAsync(upgradeFormat, cancellationToken).ConfigureAwait(false),
        ListDestinationsCommand => ListDestinations(),
        UpsertDestinationCommand upsertDestination => UpsertDestination(upsertDestination),
        DeleteDestinationCommand deleteDestination => DeleteDestination(deleteDestination),
        GetServiceSettingsCommand => GetServiceSettings(),
        UpdateServiceSettingsCommand updateSettings => UpdateServiceSettings(updateSettings),
        ListPairingsCommand => ListPairings(),
        GetDiagnosticsCommand => GetDiagnostics(),
        SetLogLevelCommand setLevel => SetLogLevel(setLevel),
        ReadLogCommand readLog => ReadLog(readLog),
        ExportDiagnosticsCommand export => await ExportDiagnosticsAsync(export, cancellationToken).ConfigureAwait(false),
        BrowseFoldersCommand browse => BrowseFolders(browse),
        ValidateSetDraftCommand draft => ValidateSetDraft(draft),
        CreatePairingInviteCommand invite => CreatePairingInvite(invite),
        ListPairingInvitesCommand => ListPairingInvites(),
        RevokePairingInviteCommand revoke => RevokePairingInvite(revoke),
        PairWithInviteCommand pair => await PairWithInviteAsync(pair, cancellationToken).ConfigureAwait(false),
        ListNoticesCommand listNotices => ListNotices(listNotices),
        AcknowledgeNoticeCommand acknowledge => AcknowledgeNotice(acknowledge),
        UnpairCommand unpair => await UnpairAsync(unpair, cancellationToken).ConfigureAwait(false),
        ListReplicaAttributionsCommand => ListReplicaAttributions(),
        ReattributeReplicaCommand reattribute => ReattributeReplica(reattribute),
        AcknowledgeReplicaClaimCommand acknowledge => AcknowledgeReplicaClaim(acknowledge),
        ListReceiptsCommand listReceipts => ListReceipts(listReceipts),
        RunBackupCommand run => RunBackup(run),
        CancelJobCommand cancel => CancelJob(cancel),
        ListJobsCommand list => ListJobs(list),
        ListSnapshotsCommand => await ListSnapshotsAsync(cancellationToken).ConfigureAwait(false),
        ListDirectoryCommand list => await ListDirectoryAsync(list, cancellationToken).ConfigureAwait(false),
        CloseRestoreSourceCommand close => await CloseRestoreSourceAsync(close).ConfigureAwait(false),
        ProvisionWriteOnlySetCommand provision =>
            await ProvisionWriteOnlySetAsync(provision, cancellationToken).ConfigureAwait(false),
        ProvisionInstallationCommand setup => ProvisionInstallation(setup),
        DiscoverArchivesCommand discover => await DiscoverArchivesAsync(discover, cancellationToken).ConfigureAwait(false),
        AdoptArchiveCommand adopt => await AdoptArchiveAsync(adopt, cancellationToken).ConfigureAwait(false),
        PreviewAdoptionCommand preview => await PreviewAdoptionAsync(preview, cancellationToken).ConfigureAwait(false),
        SyncCommand sync => await SyncAsync(sync, cancellationToken).ConfigureAwait(false),
        VerifyDestinationCommand deep =>
            await VerifyDestinationAsync(deep, cancellationToken).ConfigureAwait(false),
        GetStatusCommand => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
        ExportConfigurationCommand => new ConfigurationResult(runtime.Configuration.ExportJson()),
        DescribeServiceCommand => Describe(),
        RestartServiceCommand => RestartService(),

        // The read paths are handled before this dispatch, on the reader lane.
        _ => new ServiceError(ServiceErrorReason.InvalidArgument, $"Unknown command '{command.GetType().Name}'."),
    };

    private ServiceResult RestartService()
    {
        // A paired console must not cut a machine it cannot see (ADR-0028
        // §6) — and this verb would sever the very connection carrying its
        // own refusal.
        if (Scope == CallerScope.Remote)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                "Restarting the service is a local decision — a paired console may watch this service "
                + "but not cut it off from the machine that owns it (ADR-0028 §6).");
        }

        if (requestRestart is null)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                "This service has no host to recycle it — a --once run, or a directly hosted handler. "
                + "Restart the process the way it was started.");
        }

        // Acknowledge FIRST: the reply must reach the wire before the host
        // tears the listener down under it. The signal only sets the host's
        // flag; the actual teardown starts after this answer is flushed.
        requestRestart();
        return new AcknowledgedResult();
    }

    /// <summary>Parses a verify level, or says what the vocabulary is.</summary>

    /// <summary>
    /// Whether a direct-ship capture can write to this destination at all —
    /// the local-path store, or the peer write adapter
    /// ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md)). The reserved
    /// cloud kinds (FR-DEST-005) are modelled by the configuration and served
    /// by nothing.
    /// </summary>
    /// <param name="destination">The destination's declaration, or null when the reference dangles.</param>
    private static bool ShipsDirectly(DestinationConfiguration? destination) =>
        destination?.Kind is DestinationKind.LocalPath or DestinationKind.Peer;
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
    private async ValueTask<(BackupSetConfiguration Set, ArchiveHandle Archive)?> FindArchiveBySnapshotAsync(
        byte[] snapshotId, CancellationToken cancellationToken)
    {
        foreach (var (set, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            using var catalogue = archive.OpenReadCatalogue();
            if (catalogue.EnumerateSnapshots().Any(row => row.SnapshotId.Span.SequenceEqual(snapshotId)))
            {
                return (set, archive);
            }
        }

        return null;
    }

    /// <summary>The subtree prefixes a restore command names; several win over one (ADR-0041).</summary>
    private static IReadOnlyList<string> PrefixesOf(IReadOnlyList<string>? paths, string? path) =>
        paths is { Count: > 0 } ? paths
        : path is { Length: > 0 } ? [path]
        : [];

    /// <summary>Plans a restore without performing it — a catalogue walk plus one store probe per located blob.</summary>
    private async ValueTask<ServiceResult> PlanRestoreAsync(PlanRestoreCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        if (RefuseUnproved(command.Source, command.SessionId, setId: null, "Planning a restore") is { } unproved)
        {
            return unproved;
        }

        var (context, error) = await ResolveRestoreContextAsync(
            command.Source, snapshotId, command.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return error!;
        }

        if (context.Source is { } source)
        {
            await source.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var catalogue = context.OpenCatalogue();
            var target = RestoreTargetProfile.ForLocalPlatform(catalogue, snapshotId, runtime.State.DeviceId);
            var plan = RestorePlanner.Plan(catalogue, snapshotId, PrefixesOf(command.Paths, command.Path), target);

            if (plan.Items.Count == 0)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound,
                    $"The catalogue knows nothing under snapshot {command.SnapshotId}"
                    + $"{(command.Path is { Length: > 0 } path ? $" at '{path}'" : string.Empty)}.");
            }

            // Measured only against where the run would write, which a
            // pre-1.53 client never names: without it there is nothing to
            // measure against.
            IReadOnlyList<(RestorePlan Plan, string OutputDirectory, string LabelPrefix)>? slices = null;
            var mode = RestoreDestinationMode.Quarantine;
            var policy = ExistingDestinationPolicy.Preserve;
            if (command.OutputDirectory is { Length: > 0 } || command.Target == "original")
            {
                var invalidShape = TryParseRestoreShape(
                    command.Target, command.Existing, command.InPlace, command.OutputDirectory,
                    out var toOriginal, out mode, out policy);
                if (invalidShape is not null)
                {
                    return invalidShape;
                }

                var (resolved, refusal) = SlicesFor(catalogue, snapshotId, plan, toOriginal, command.OutputDirectory);
                if (resolved is null)
                {
                    return refusal!;
                }

                slices = resolved;
            }

            var probed = await ProbePlanAsync(context, catalogue, plan, cancellationToken).ConfigureAwait(false);

            // What the files write and what they carry, from the manifests
            // the probe has already read.
            var writeBytes = plan.Items
                .Where(item => item.Kind == EntryKind.File)
                .Aggregate(0ul, (sum, item) => sum
                    + (probed.Facts.TryGetValue(item.ObjectId, out var known) ? known.WrittenBytes : item.Length));
            List<string> conflicts = [.. plan.Conflicts.Select(conflict => $"{conflict.Path} — {conflict.Reason}")];
            List<string> degradations =
            [
                .. plan.Degradations.Concat(RestoreMetadata.Declare(plan, probed.Facts, target))
                    .Select(degradation => degradation.Detail),
            ];

            IReadOnlyList<RestoreSpaceDescriptor>? space = null;
            if (slices is not null)
            {
                var report = await RestoreSpace.MeasureAsync(
                    [.. slices.Select(slice => new RestoreSlice(slice.Plan, slice.OutputDirectory))],
                    mode, policy, runtime.RestoreSpaceProbe, RestoreSpace.WrittenBytesFrom(probed.Facts),
                    cancellationToken).ConfigureAwait(false);
                space =
                [
                    .. report.Needs.Select(need => new RestoreSpaceDescriptor(
                        need.Directory, (long)need.NeededBytes, need.AvailableBytes, need.Working)),
                ];

                // A conflict too, where a client that predates the space
                // figure already looks.
                conflicts.AddRange(report.Shortfalls.Select(need =>
                    $"{need.Directory} — the restore would not fit: it {need.Describe()}"));
            }

            return new RestorePlanResult(
                plan.Items.Count(item => item.Kind != EntryKind.DirectoryPlaceholder),
                (long)plan.SpaceEstimateBytes,
                probed.Missing,
                conflicts.Count,
                conflicts.Count == 0 ? null : [.. conflicts.Take(20)],
                degradations.Count == 0 ? null : degradations,
                (long)writeBytes,
                space);
        }
        finally
        {
            context.Source?.Gate.Release();
        }
    }

    /// <summary>
    /// The run's shape, as a plan or a run names it: whether it writes back
    /// to the captured roots, where restored content lands, and what happens
    /// to a file already there. One reading for both, so a plan answers for
    /// the run it plans (FR-RST-003).
    /// </summary>
    private static ServiceError? TryParseRestoreShape(
        string? target,
        string? existing,
        bool inPlace,
        string? outputDirectory,
        out bool toOriginal,
        out RestoreDestinationMode mode,
        out ExistingDestinationPolicy policy)
    {
        toOriginal = target == "original";
        mode = inPlace || toOriginal ? RestoreDestinationMode.InPlace : RestoreDestinationMode.Quarantine;
        policy = ExistingDestinationPolicy.Preserve;

        if (target is not (null or "folder" or "original"))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, $"'{target}' is not a restore target (folder | original).");
        }

        if (!toOriginal && string.IsNullOrWhiteSpace(outputDirectory))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "A restore needs an output directory.");
        }

        switch (existing)
        {
            case null:
                return null;
            case "rename":
                policy = ExistingDestinationPolicy.WriteBeside;
                return null;
            case "overwrite":
                policy = ExistingDestinationPolicy.Replace;
                return null;
            default:
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{existing}' is not an existing-file policy (rename | overwrite).");
        }
    }

    /// <summary>
    /// The run's slices: the whole plan into the folder, or one slice per
    /// captured root for the original location (ADR-0041).
    /// </summary>
    private (IReadOnlyList<(RestorePlan Plan, string OutputDirectory, string LabelPrefix)>? Slices, ServiceError? Refusal)
        SlicesFor(
            Repository.Catalogue.Catalogue catalogue,
            byte[] snapshotId,
            RestorePlan plan,
            bool toOriginal,
            string? outputDirectory) =>
        toOriginal
            ? SliceForOriginal(catalogue, snapshotId, plan)
            : ([(plan, outputDirectory!, string.Empty)], null);

    /// <summary>
    /// What a plan is for: the objects it needs and cannot find, reported
    /// before any byte moves rather than discovered part-way through — and
    /// the blob set a run opens instead of every footer in the store.
    /// </summary>
    /// <remarks>
    /// The work lives in <see cref="Restore.RestoreBlobSet"/>, beside the
    /// planner, because every restore path needs it and only this one had
    /// it: the CLI's direct restore opened every footer in the store, and so
    /// did this handler whenever the source was local.
    /// </remarks>
    private async ValueTask<Restore.RestoreBlobSetResult> ProbePlanAsync(
        RestoreContext context,
        Repository.Catalogue.Catalogue catalogue,
        RestorePlan plan,
        CancellationToken cancellationToken)
    {
        // A plan answers for the run it plans: one that will read around what
        // its own store does not hold counts a file missing only when no copy
        // of the set holds what it needs (FR-RST-007).
        await using var copies = context.OtherCopies(runtime);
        return copies is null
            ? await Restore.RestoreBlobSet.ResolveAsync(
                catalogue, plan, context.Store, context.RepositoryId, context.Keys, cancellationToken)
                .ConfigureAwait(false)
            : await Restore.RestoreBlobSet.ResolveAsync(
                catalogue, plan, context.Store, copies.Sources, context.RepositoryId, context.Keys, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Performs a restore, writing on this machine (ADR-0028 §6).</summary>
    private async ValueTask<ServiceResult> RunRestoreAsync(RunRestoreCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        var invalidShape = TryParseRestoreShape(
            command.Target, command.Existing, command.InPlace, command.OutputDirectory,
            out var toOriginal, out var mode, out var existing);
        if (invalidShape is not null)
        {
            return invalidShape;
        }

        if (RefuseUnproved(command.Source, command.SessionId, setId: null, "A restore") is { } unproved)
        {
            return unproved;
        }

        var (context, error) = await ResolveRestoreContextAsync(
            command.Source, snapshotId, command.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return error!;
        }

        if (context.Source is { } source)
        {
            await source.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var catalogue = context.OpenCatalogue();
            var target = RestoreTargetProfile.ForLocalPlatform(catalogue, snapshotId, runtime.State.DeviceId);
            var plan = RestorePlanner.Plan(catalogue, snapshotId, PrefixesOf(command.Paths, command.Path), target);
            if (plan.Items.Count == 0)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound,
                    $"The catalogue knows nothing under snapshot {command.SnapshotId}.");
            }

            // The original-location mapping is resolved BEFORE anything is
            // read or written (plan-before-transfer): every top-level slice
            // must name a configured root, or the run is refused whole.
            var (slices, refusal) = SlicesFor(catalogue, snapshotId, plan, toOriginal, command.OutputDirectory);
            if (slices is null)
            {
                return refusal!;
            }

            using var reader = new RepositoryReader(
                context.RepositoryId, context.Keys, context.Store, context.Source?.ReadAuthority);

            // No load at all: the catalogue already says where every record
            // is, so a record is read from there and a blob is opened through
            // its footer only when that fails (NFR-PERF-009). Opening the
            // blobs a plan needs cost three ranged reads each before a byte of
            // payload, which no amount of coalescing afterwards could get
            // under the budget. The plan verb still runs the probe — naming
            // unreachable paths before anything moves is its job, not this
            // one's (FR-RST-003).
            reader.UseLocationSource(catalogue.ResolveLocation);

            // The set's own archive reads around what its own store will not
            // serve (FR-RST-007): the set's other copies, nearest first, each
            // opened only once every earlier one has failed. A destination
            // named as the source is read alone.
            await using var copies = context.OtherCopies(runtime);
            if (copies is not null)
            {
                reader.UseOtherCopies(context.OwnCopyName, copies.Sources);
            }

            var options = new RestoreExecutionOptions
            {
                DestinationMode = mode,
                ExistingDestination = existing,

                // A fresh identifier per run, not per snapshot: two restores of
                // one snapshot must displace into distinct stores, or the second
                // overwrites the first's displaced copies — the single shared
                // refuge architecture 08 §3.1 forbids. A snapshot-derived id
                // made every restore of a snapshot share one.
                RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // Whether the whole run fits, before its first slice writes a byte
            // (FR-RST-003): every slice is measured together, because two on
            // one volume share its room. The figure errs high, and a volume
            // that compresses what it stores holds more than it says, so a
            // person may go on regardless.
            var space = await RestoreSpace.MeasureRunAsync(
                [.. slices.Select(slice => new RestoreSlice(slice.Plan, slice.OutputDirectory))],
                options.DestinationMode, options.ExistingDestination, runtime.RestoreSpaceProbe, reader,
                cancellationToken).ConfigureAwait(false);
            if (space.IsShort && !command.IgnoreFreeSpace)
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"{space.Refusal()} Nothing was written. Free some space or choose another folder, or restore "
                    + "anyway, ignoring free space: a volume that compresses what it stores can hold more than this estimate.");
            }

            // The output directory is a path on the machine running the
            // service: a restore commanded from elsewhere writes here and the
            // caller is told what happened, never sent the files (ADR-0028 §6).
            var executor = new RestoreExecutor(reader, target, runtime.LoggerFor<RestoreExecutor>());
            var receipts = new List<(RestoreReceipt Receipt, string LabelPrefix)>();
            foreach (var (slice, outputDirectory, labelPrefix) in slices)
            {
                receipts.Add((
                    await executor.ExecuteAsync(slice, outputDirectory, options, cancellationToken).ConfigureAwait(false),
                    labelPrefix));
            }

            var receipt = MergeReceipts(receipts);
            var receiptPath = PersistReceipt(receipt, options.RunId);
            if (copies is not null)
            {
                RecordReadAroundFindings(context.Set!, context.Archive!, copies, reader, options.NowUnixMilliseconds);
            }

            // What a person needs to hear of: a file that came from another
            // copy because a copy passed over was damaged or would not read.
            // One that came from a destination only because staging no longer
            // holds it came from where its bytes were meant to come from.
            var readAround = receipt.Items.Where(item => item.ReadAround is not null).ToList();

            // Files, not entries. The receipt records a created directory as
            // "restored" too, but the contract documents this field as files
            // written — and PlanRestore counts the same way, so a caller
            // comparing the plan against the outcome is comparing like with
            // like.
            var directories = plan.Items
                .Where(item => item.Kind == EntryKind.DirectoryPlaceholder)
                .Select(item => item.Path)
                .ToHashSet(StringComparer.Ordinal);

            var failures = receipt.Items.Where(item => item.Outcome == "failed").ToList();
            var notApplied = RestoreMetadata.Summarise(receipt.Items, target);
            return new RestoreResult(
                // A degraded file's content was written and verified, so it
                // counts among the files written; the Outcome carries the
                // shortfall (Partial), exactly as it carries a skipped
                // symlink's.
                receipt.Items.Count(item => item.Outcome is "restored" or "degraded" && !directories.Contains(item.Path)),
                failures.Count,
                // Where the files actually are, not where the caller pointed.
                // Historical content quarantines by default (FR-RST-006), so
                // the two differ, and a caller told the wrong one cannot find
                // its data.
                receipt.WrittenTo,
                // The outcome the executor computed — carried whole so a
                // Partial restore is not reported to a remote client as
                // success (FR-RST-005).
                receipt.Outcome.ToString().ToLowerInvariant(),
                Skipped: receipt.Items.Count(item => item.Outcome == "skipped"),
                Degraded: receipt.Items.Count(item => item.Outcome == "degraded"),
                Displaced: receipt.Displaced.Count,
                WrittenBeside: receipt.Items.Count(item => item.WrittenAs is not null),
                ReceiptPath: receiptPath,
                FailedSample: failures.Count == 0
                    ? null
                    : [.. failures.Take(20).Select(item => $"{item.Path} — {item.Detail}")],
                ReadAround: readAround.Count,
                ReadAroundSample: readAround.Count == 0
                    ? null
                    : [.. readAround.Take(20).Select(item =>
                        $"{item.Path} — read from {string.Join(", ", item.ReadFrom ?? [])}, "
                        + $"around {string.Join("; ", item.ReadAround!)}")],
                NotApplied: notApplied.Count == 0 ? null : notApplied);
        }
        finally
        {
            context.Source?.Gate.Release();
        }
    }

    /// <summary>
    /// The per-root slices of an original-location restore (ADR-0041). A
    /// single-root set restores the whole plan into its root; a multi-root
    /// set's plan paths are label-prefixed (ADR-0040), so the items group by
    /// first component, each slice's paths shed the label, and each runs
    /// into its root — refused whole, naming the label, when any slice's
    /// label no longer maps to a configured root.
    /// </summary>
    private (IReadOnlyList<(RestorePlan Plan, string OutputDirectory, string LabelPrefix)>? Slices, ServiceError? Refusal)
        SliceForOriginal(Repository.Catalogue.Catalogue catalogue, byte[] snapshotId, RestorePlan plan)
    {
        var row = catalogue.EnumerateSnapshots()
            .FirstOrDefault(candidate => candidate.SnapshotId.Span.SequenceEqual(snapshotId));
        var setId = row is null ? null : Convert.ToHexStringLower(row.BackupSetId.Span);
        var set = setId is null
            ? null
            : runtime.Configuration.BackupSets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, setId, StringComparison.Ordinal));
        if (set is null)
        {
            return (null, new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "This snapshot's set is no longer configured — restore to a chosen folder instead."));
        }

        if (set.Roots.Count == 1)
        {
            return ([(plan, set.Roots[0].Path, string.Empty)], null);
        }

        var byLabel = plan.Items
            .GroupBy(item => item.Path.Split('/')[0], StringComparer.Ordinal)
            .ToList();
        var slices = new List<(RestorePlan, string, string)>();
        foreach (var group in byLabel)
        {
            var root = set.Roots.FirstOrDefault(candidate =>
                string.Equals(candidate.Label, group.Key, StringComparison.Ordinal));
            if (root is null)
            {
                return (null, new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{group.Key}' is not one of the set's root labels any more — restore to a chosen folder instead."));
            }

            var prefix = group.Key + "/";
            var items = group
                .Where(item => item.Path.Length > prefix.Length)
                .Select(item => item with { Path = item.Path[prefix.Length..] })
                .ToList();
            slices.Add((
                new RestorePlan
                {
                    SnapshotId = plan.SnapshotId,
                    Items = items,
                    Conflicts = plan.Conflicts,
                    Degradations = plan.Degradations,
                },
                root.Path,
                prefix));
        }

        return (slices, null);
    }

    /// <summary>One receipt for the run, whichever way it was sliced.</summary>
    private static RestoreReceipt MergeReceipts(List<(RestoreReceipt Receipt, string LabelPrefix)> receipts)
    {
        if (receipts.Count == 1 && receipts[0].LabelPrefix.Length == 0)
        {
            return receipts[0].Receipt;
        }

        static string Prefixed(string prefix, string path) => prefix.Length == 0 ? path : prefix + path;
        var worst = receipts.Max(entry => entry.Receipt.Outcome);
        return new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = receipts[0].Receipt.SnapshotId,
            StartedAt = receipts.Min(entry => entry.Receipt.StartedAt),
            CompletedAt = receipts.Max(entry => entry.Receipt.CompletedAt),
            Items = [.. receipts.SelectMany(entry => entry.Receipt.Items.Select(item => item with
            {
                Path = Prefixed(entry.LabelPrefix, item.Path),
                WrittenAs = item.WrittenAs is null ? null : Prefixed(entry.LabelPrefix, item.WrittenAs),
            }))],
            Displaced = [.. receipts.SelectMany(entry =>
                entry.Receipt.Displaced.Select(path => Prefixed(entry.LabelPrefix, path)))],
            WrittenTo = string.Join("; ", receipts.Select(entry => entry.Receipt.WrittenTo)),
            Outcome = worst,
        };
    }

    /// <summary>
    /// Persists the receipt (FR-RST-004): the executor builds it and until
    /// now nobody kept it, which made "the restore succeeded" an impression
    /// rather than a checkable claim once the dialog closed.
    /// </summary>
    private string PersistReceipt(RestoreReceipt receipt, string runId)
    {
        Directory.CreateDirectory(runtime.ReceiptsRoot);
        var path = Path.Combine(runtime.ReceiptsRoot, $"{runId}.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, receipt.ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
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
        var sealedRecords = 0L;

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

                // Sealed content on a write-only set is a stated incapacity,
                // never a failure and never a silent pass (ADR-0042).
                sealedRecords += result.RecordsSealed;
            }
        }

        return new VerificationResult(examined, failures, canonical, sealedRecords);
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
                var sealedRecords = 0L;
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

                    sealedRecords += result.RecordsSealed;
                }

                // The stated incapacity, once per set rather than per blob: a
                // records-level check of a write-only set must say what it
                // could not check (ADR-0042), never read as a clean content
                // sweep — and never as damage.
                if (sealedRecords > 0)
                {
                    findings.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{set.Name}: {sealedRecords} record(s) are sealed to the repository public key — structure verified; content verification needs a restore grant (ADR-0042). Not damage."));
                }
            }

            using (var journalReader = new JournalReader(
                archive.Store, archive.Repository.RepositoryId, archive.Repository.Credential))
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
            [.. runtime.Configuration.BackupSets.Select(set =>
            {
                var facts = LocalDescriptorOf(set);
                return new BackupSetDescriptor(
                    set.Id, set.Name,
                    // Root carries the first root for pre-1.10 clients; Roots is
                    // the whole truth (ADR-0040).
                    set.Roots[0].Path,
                    set.Schedule, set.IncludeRules, set.ExcludeRules,
                    [.. set.Destinations.Select(reference => reference.Ref)],
                    ToPolicyDescriptor(set.Retention),
                    ToOverrideDescriptors(set.Destinations),
                    [.. set.Roots.Select(root => new BackupRootDescriptor(root.Path, root.Label))],
                    set.Priority,
                    set.DirectShip,
                    // The archive's own derivation facts (contract 1.30): an
                    // adopted set's differ from the installation's.
                    facts is null ? null : Convert.ToHexStringLower(facts.KdfSalt.Span),
                    facts?.KdfParameters.MemoryKiB,
                    facts?.KdfParameters.Iterations,
                    facts?.KdfParameters.Parallelism,
                    facts is null ? null : Convert.ToHexStringLower(facts.SealingPublicKey.Span));
            })]);

    private async ValueTask<ServiceResult> UpsertBackupSetAsync(
        UpsertBackupSetCommand command, CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;

        // The schedule is validated here, at the command boundary, and not in
        // configuration load — ADR-0035 §1's blast-radius rule: a throw on
        // the load path stops every set backing up over one typo. Unvalidated,
        // a bad schedule saves cleanly and fails permanently at the next
        // pass, which is the worse discovery (ADR-0037 §2).
        if (ScheduleDefect(command.Set.Schedule) is { } scheduleDefect)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, scheduleDefect);
        }

        // The roots (ADR-0040): a 1.10 client speaks `roots`, older ones the
        // single `root`; the labels a multi-root set needs are materialised
        // HERE, once, and persisted — deriving them on read would let a later
        // sibling silently shift an existing root's coordinates.
        IReadOnlyList<BackupRootConfiguration> requestedRoots =
            command.Set.Roots is { Count: > 0 } draftRoots
                ? [.. draftRoots.Select(root => new BackupRootConfiguration { Path = root.Path, Label = root.Label })]
                : !string.IsNullOrWhiteSpace(command.Set.Root)
                    ? [new BackupRootConfiguration { Path = command.Set.Root }]
                    : [];
        if (requestedRoots.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "A backup set needs at least one root folder.");
        }

        var resolvedRoots = ClientConfiguration.DeriveLabels(requestedRoots);

        // What the command does not carry is preserved, never zeroed: a 1.6
        // client's upsert leaves retention exactly as it stood. A carried
        // policy with every field absent is the explicit "none" (ADR-0037).
        var existing = configuration.BackupSets
            .FirstOrDefault(set => string.Equals(set.Id, command.Set.Id, StringComparison.Ordinal));

        // The storage shape (ADR-0046, contract 1.23): null preserves — a
        // pre-1.23 client cannot see the field and must not convert a set —
        // and an explicit value sets it.
        //
        // What a NEW set defaults to is a judgement rather than a capability
        // question, and the two came apart with the peer write adapter
        // ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md)). A peer can
        // now be shipped to directly, so the refusal below asks only whether
        // the set references anything the sink can write to at all. The
        // default still asks for a LOCAL PATH, because for a set whose only
        // destination is a peer the staging archive is buying three things
        // direct-ship gives up: a capture that does not wait on the link, a
        // transfer that resumes after the link dies mid-object, and — the one
        // that matters most — an independent copy to check the replica's
        // content against, without which no pass can honestly call it
        // verified (ADR-0058 §8). Direct-ship remains available to a peer-only
        // set as a stated choice, for a machine with no room for the second
        // copy; it is not one to make on a person's behalf.
        var directShip = command.Set.DirectShip
            ?? existing?.DirectShip
            ?? command.Set.Destinations.Any(name =>
                configuration.FindDestination(name)?.Kind == DestinationKind.LocalPath);
        if (directShip && !command.Set.Destinations.Any(name =>
                ShipsDirectly(configuration.FindDestination(name))))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "A direct-ship set needs at least one local-path or peer destination — a capture with "
                + "nowhere to ship has nothing it can promise (ADR-0046).");
        }

        // Changing the shape re-homes the set's repository: the archive
        // handle is swapped out beneath whoever holds it, so a live run must
        // finish or be cancelled first — the delete-set rule, applied to the
        // one edit with the same blast radius.
        var shapeChanged = existing is not null && existing.DirectShip != directShip;
        if (shapeChanged)
        {
            var lastJob = runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == existing!.Id);
            if (lastJob is not null && !JobStateStore.HasSettled(lastJob.State) && runtime.Queue.IsActive(lastJob.Id))
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"Backup set '{existing!.Name}' has a run in progress — the storage shape cannot change "
                    + "under a live run. Cancel it or let it finish, then save again.");
            }
        }

        // The 1↔N transitions change the rule coordinate system (ADR-0040):
        // growing past one root prefixes the old root's anchored rules with
        // its new label; shrinking back strips the survivor's. Only rules the
        // set already had are rewritten — a rule the client just sent is
        // trusted to speak the new coordinates.
        var (includeRules, excludeRules, reanchored) = ReanchorRules(
            existing, resolvedRoots, command.Set.IncludeRules, command.Set.ExcludeRules);

        var replacement = new BackupSetConfiguration
        {
            Id = command.Set.Id,
            Name = command.Set.Name,
            Roots = resolvedRoots,
            Schedule = command.Set.Schedule,
            IncludeRules = includeRules,
            ExcludeRules = excludeRules,
            Retention = ToRetention(command.Set.Retention, existing?.Retention),
            // Null preserves (a pre-1.17 client cannot see the field); zero
            // is the explicit default a 1.17 client may set back.
            Priority = command.Set.Priority ?? existing?.Priority,
            DirectShip = directShip,
            Destinations = [.. command.Set.Destinations.Select(name => new SetDestinationReference
            {
                Ref = name,
                // The per-reference priority (ADR-0047 §4) has no wire field
                // either; preserved by name, exactly like the retention
                // override below.
                Priority = existing?.Destinations.FirstOrDefault(reference =>
                    string.Equals(reference.Ref, name, StringComparison.Ordinal))?.Priority,
                Retention = command.Set.DestinationRetention is { } overrides
                    // A carried map is the complete truth: named entries set
                    // (empty clears), unnamed destinations carry no override.
                    ? ToRetention(overrides.GetValueOrDefault(name), existing: null)
                    // No map at all preserves whatever the set held, by name.
                    : existing?.Destinations.FirstOrDefault(reference =>
                        string.Equals(reference.Ref, name, StringComparison.Ordinal))?.Retention,
            })],
        };

        // The circular-capture guard (FR-DEST-011), judged on the resolved
        // roots and the re-anchored rules — exactly what the set will walk.
        // At the boundary and never at load: an installation already carrying
        // the layout keeps loading, and the edit that would keep it is what
        // gets refused.
        var circular = CircularCapture.Defects(
            [replacement], configuration.Destinations, ServiceStorage());
        if (circular.Count > 0)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, string.Join(" ", circular));
        }

        // The condition of choosing a local destination (ADR-0051,
        // FR-DEST-017), refused on the first binding that fails it. A Debug
        // build lets every such binding stand and says so in the answer
        // (Amendment 2).
        var conflicts = PlacementConflicts(
                configuration, existing, [.. resolvedRoots.Select(root => root.Path)], command.Set.Destinations)
            .ToList();
        if (conflicts.Count > 0 && !runtime.AllowsSameDrivePlacement)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, PlacementRefusal(conflicts[0].Destination, conflicts[0].Conflict));
        }

        List<string> allowed = [.. conflicts.Select(chosen => PlacementAllowed(chosen.Destination, chosen.Conflict))];

        // Replace in place: the first set is the default RunBackupCommand
        // runs, and status renders declaration order — an edit must not
        // reshuffle either (ADR-0037 §5).
        var sets = configuration.BackupSets.ToList();
        var index = sets.FindIndex(set => string.Equals(set.Id, replacement.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            sets[index] = replacement;
        }
        else
        {
            sets.Add(replacement);
        }

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

        if (shapeChanged)
        {
            // The cached handle still speaks the old shape; evicted, the next
            // open reads the flag fresh — and a staging set flipped on
            // migrates there and then, in this process, no restart needed.
            await runtime.EvictArchiveAsync(replacement.Id, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.Now;
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();

        if (shapeChanged && directShip)
        {
            // Seed at once rather than at the next scheduler pass: a flipped
            // set's next capture refuses until a destination holds its full
            // history (the sink's scope rule), and a person who just flipped
            // will press "Back up now" before any pass has run. The gained-
            // destination path queues its seed immediately for the same
            // reason (ADR-0047).
            FanOut.EnqueueAll(runtime, replacement, now, userInitiated: true);
        }

        if (existing is null)
        {
            // Saving a new set IS asking for its first backup (ADR-0047): the
            // capture queues now and the fan-out follows it, so the
            // destinations populate without waiting for a schedule or a
            // person remembering to run one.
            foreach (var reference in replacement.Destinations)
            {
                runtime.DestinationSync.RecordNeedsFull(replacement.Id, reference.Ref, nowMs);
            }

            var first = Scheduler.Enqueue(runtime, replacement, now, userInitiated: true);
            _ = first.ContinueWith(
                completed =>
                {
                    if (completed is { Status: TaskStatus.RanToCompletion, Result.Outcome: "ran" })
                    {
                        FanOut.EnqueueAll(runtime, replacement, now, userInitiated: true);
                    }
                },
                TaskScheduler.Default);

            return new ConfigurationChangeResult(
            [
                $"Backup set '{replacement.Name}' created.",
                $"First backup queued as job {Scheduler.LatestJobFor(runtime, replacement.Id) ?? "(pending)"}; "
                    + "its destinations receive the archive when it completes.",
                .. allowed,
            ]);
        }

        // The destinations this edit newly references owe a full copy
        // (ADR-0047): flagged in the ledger, and seeded now rather than at
        // the next pass — referencing a destination IS asking it to hold the
        // set.
        var gained = replacement.Destinations
            .Where(reference => !existing.Destinations.Any(before =>
                string.Equals(before.Ref, reference.Ref, StringComparison.Ordinal)))
            .Select(reference => reference.Ref)
            .ToList();

        var seeded = new List<string>();
        foreach (var name in gained)
        {
            runtime.DestinationSync.RecordNeedsFull(replacement.Id, name, nowMs);
            if (runtime.ArchiveExists(replacement.Id))
            {
                _ = FanOut.Enqueue(runtime, replacement, name, now, userInitiated: true);
                seeded.Add($"Destination '{name}' is newly referenced; seeding its full copy was queued and runs now.");
            }
            else
            {
                seeded.Add($"Destination '{name}' is newly referenced; it receives its first copy when this set first backs up.");
            }
        }

        // A material edit — the root or the rules — changes what the next
        // snapshot will hold, so it is answered with what changed and, when
        // there is a last backup to compare with, a rescan is queued whose
        // finding stands as a notice until that next backup completes
        // (ADR-0038). Schedule and retention edits change when and how long,
        // not what, and stay a plain acknowledgement.
        if (IsMaterialChange(existing, replacement))
        {
            var lines = new List<string>();
            var oldRoots = existing.Roots.Select(root => root.Path).ToHashSet(StringComparer.Ordinal);
            var newRoots = replacement.Roots.Select(root => root.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var added in replacement.Roots.Where(root => !oldRoots.Contains(root.Path)))
            {
                lines.Add(replacement.Roots.Count > 1
                    ? $"root added: '{added.Path}' as '{added.Label}'"
                    : $"root changed to: '{added.Path}'");
            }

            foreach (var removed in existing.Roots.Where(root => !newRoots.Contains(root.Path)))
            {
                lines.Add($"root removed: '{removed.Path}'");
            }

            if (!existing.IncludeRules.SequenceEqual(replacement.IncludeRules, StringComparer.Ordinal))
            {
                lines.Add($"include rules changed ({existing.IncludeRules.Count} -> {replacement.IncludeRules.Count})");
            }

            if (!existing.ExcludeRules.SequenceEqual(replacement.ExcludeRules, StringComparer.Ordinal))
            {
                lines.Add($"exclude rules changed ({existing.ExcludeRules.Count} -> {replacement.ExcludeRules.Count})");
            }

            if (reanchored)
            {
                lines.Add(
                    "Saved rules were re-anchored to the new root coordinates — anchored rules gained or "
                    + "lost the root's label prefix so they keep meaning what they meant.");
            }

            if (runtime.ArchiveExists(replacement.Id))
            {
                SetChangeScan.Enqueue(runtime, replacement);
                lines.Add(
                    "A rescan against the last backup was queued; its findings will stand as a notice " +
                    "until the next backup completes.");
            }
            else
            {
                lines.Add("This set has not backed up yet; its first backup captures under these settings.");
            }

            lines.AddRange(seeded);
            lines.AddRange(allowed);
            return new ConfigurationChangeResult(lines);
        }

        List<string> said = [.. seeded, .. allowed];
        return said.Count > 0 ? new ConfigurationChangeResult(said) : new AcknowledgedResult();
    }

    /// <summary>
    /// Whether an edit changed what the next snapshot will hold — the roots
    /// or the rules, compared as sets so mere reordering is not material.
    /// </summary>
    private static bool IsMaterialChange(BackupSetConfiguration existing, BackupSetConfiguration replacement) =>
        !existing.Roots.Select(root => (root.Path, root.Label))
            .ToHashSet()
            .SetEquals(replacement.Roots.Select(root => (root.Path, root.Label)))
        || !existing.IncludeRules.ToHashSet(StringComparer.Ordinal).SetEquals(replacement.IncludeRules)
        || !existing.ExcludeRules.ToHashSet(StringComparer.Ordinal).SetEquals(replacement.ExcludeRules);

    /// <summary>
    /// The bindings of a set that fail the condition of choosing a local
    /// destination (ADR-0051, FR-DEST-017), one per destination, in the order
    /// the destinations are named. The save refuses with the first, and a
    /// draft names them all (ADR-0037 Amendment 2), each worded by
    /// <see cref="PlacementRefusal"/> so the two say the same thing in the
    /// same words. A Debug build words them by <see cref="PlacementAllowed"/>
    /// instead, in the save's answer and the draft's warnings alike
    /// (ADR-0051 Amendment 2).
    /// </summary>
    /// <param name="configuration">The configuration the destinations are declared in.</param>
    /// <param name="existing">The set as the configuration holds it, or null for a new set.</param>
    /// <param name="rootPaths">The set's roots, in the order it names them.</param>
    /// <param name="destinationNames">The destinations the set references.</param>
    /// <returns>Each failing binding: the destination's name, and the root it shares a drive with.</returns>
    /// <remarks>
    /// A destination must sit on a different volume than every root, and on
    /// a different physical drive where the platform can say, or the backup
    /// dies with the files it protects. Only the bindings a save chooses are
    /// judged: a newly referenced destination, or every local one when the
    /// roots change. A standing binding in an older configuration keeps
    /// loading and keeps its status warnings (ADR-0035). It is the choosing
    /// that is gated.
    /// </remarks>
    private IEnumerable<(string Destination, PlacementConflict Conflict)> PlacementConflicts(
        ClientConfiguration configuration,
        BackupSetConfiguration? existing,
        IReadOnlyList<string> rootPaths,
        IEnumerable<string> destinationNames)
    {
        var rootsChanged = existing is null
            || existing.Roots.Count != rootPaths.Count
            || existing.Roots.Zip(rootPaths).Any(pair =>
                !string.Equals(pair.First.Path, pair.Second, StringComparison.Ordinal));
        foreach (var name in destinationNames)
        {
            if (configuration.FindDestination(name) is not
                { Kind: DestinationKind.LocalPath, Path: { Length: > 0 } destinationPath })
            {
                continue;
            }

            var newlyChosen = existing is null || !existing.Destinations.Any(reference =>
                string.Equals(reference.Ref, name, StringComparison.Ordinal));
            if (!newlyChosen && !rootsChanged)
            {
                continue;
            }

            if (LocalDestinationPlacement.Judge(
                    rootPaths, destinationPath, runtime.VolumeIdOf, runtime.DiskIdOf) is { } conflict)
            {
                yield return (name, conflict);
            }
        }
    }

    /// <summary>The refusal a binding that fails the placement condition gets (ADR-0051).</summary>
    private static string PlacementRefusal(string destination, PlacementConflict conflict) =>
        $"{BindingConflict(destination, conflict)} — a backup on the drive the files live on dies with them. "
        + "Choose a local destination on a different drive (ADR-0051).";

    /// <summary>What a Debug build says of the same binding as it lets it stand (ADR-0051 Amendment 2).</summary>
    private static string PlacementAllowed(string destination, PlacementConflict conflict) =>
        AllowedOnlyInDebug(BindingConflict(destination, conflict));

    private static string BindingConflict(string destination, PlacementConflict conflict) =>
        $"Destination '{destination}' shares {(conflict.SamePhysicalDisk ? "a physical drive" : "a volume")} "
        + $"with root '{conflict.Root}'";

    /// <summary>
    /// A placement a Release build refuses, said as a Debug build lets it
    /// stand (ADR-0051 Amendment 2): the conflict, that only this build
    /// allows it, and the reason the other refuses.
    /// </summary>
    private static string AllowedOnlyInDebug(string conflict) =>
        $"{conflict}, which only a Debug build allows: a Release build refuses it, because a backup on the drive "
        + "the files live on dies with them (ADR-0051).";

    /// <summary>
    /// The 1↔N coordinate transitions (ADR-0040), applied per rule and only
    /// to rules the set already carried: growing past one root prefixes the
    /// old root's anchored rules (those containing <c>/</c>) with its new
    /// label; shrinking to one strips the survivor's prefix — a stripped rule
    /// left with no <c>/</c> becomes an exact-path regex, because a bare name
    /// is the any-depth shorthand and would silently widen. Shorthand rules
    /// are depth-independent and never touched.
    /// </summary>
    private static (IReadOnlyList<string> Includes, IReadOnlyList<string> Excludes, bool Reanchored) ReanchorRules(
        BackupSetConfiguration? existing,
        IReadOnlyList<BackupRootConfiguration> resolvedRoots,
        IReadOnlyList<string> includeRules,
        IReadOnlyList<string> excludeRules)
    {
        if (existing is null)
        {
            return (includeRules, excludeRules, false);
        }

        Func<string, string>? transform = null;
        if (existing.Roots.Count == 1 && resolvedRoots.Count > 1
            && resolvedRoots.FirstOrDefault(root =>
                string.Equals(root.Path, existing.Roots[0].Path, StringComparison.Ordinal)) is { Label: { } label })
        {
            transform = rule =>
                !rule.StartsWith("re:", StringComparison.Ordinal) && rule.Contains('/', StringComparison.Ordinal)
                    ? label + "/" + rule
                    : rule;
        }
        else if (existing.Roots.Count > 1 && resolvedRoots.Count == 1
            && existing.Roots.FirstOrDefault(root =>
                string.Equals(root.Path, resolvedRoots[0].Path, StringComparison.Ordinal)) is { Label: { } survivor })
        {
            var prefix = survivor + "/";
            transform = rule =>
            {
                if (rule.StartsWith("re:", StringComparison.Ordinal)
                    || !rule.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return rule;
                }

                var stripped = rule[prefix.Length..];
                if (stripped.Contains('/', StringComparison.Ordinal))
                {
                    return stripped;
                }

                // A single component would read as **/<name>; an exact-path
                // regex keeps the top-level anchoring — unless it carries
                // glob syntax, which has no mechanical regex twin, where the
                // glob stays and honestly widens. Escaped by hand: the
                // dialect's subset refuses backslash-alphanumeric escapes,
                // which Regex.Escape emits for whitespace.
                return stripped.AsSpan().IndexOfAny('*', '?') >= 0
                    ? stripped
                    : "re:" + EscapeRegexLiteral(stripped);
            };
        }

        if (transform is null)
        {
            return (includeRules, excludeRules, false);
        }

        var oldIncludes = existing.IncludeRules.ToHashSet(StringComparer.Ordinal);
        var oldExcludes = existing.ExcludeRules.ToHashSet(StringComparer.Ordinal);
        var reanchored = false;

        List<string> Apply(IReadOnlyList<string> rules, HashSet<string> saved)
        {
            var result = new List<string>(rules.Count);
            foreach (var rule in rules)
            {
                var next = saved.Contains(rule) ? transform(rule) : rule;
                reanchored |= !string.Equals(next, rule, StringComparison.Ordinal);
                result.Add(next);
            }

            return result;
        }

        return (Apply(includeRules, oldIncludes), Apply(excludeRules, oldExcludes), reanchored);
    }

    /// <summary>Escapes a literal for a rules-v1 regex rule — metacharacters only, staying inside the pinned subset.</summary>
    private static string EscapeRegexLiteral(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 4);
        foreach (var character in text)
        {
            if (character is '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '^' or '$')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
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
        // Local time on purpose: schedule arithmetic downstream works in the
        // operator's wall clock (the DST rules depend on it), and every
        // durable stamp goes through ToUnixTimeMilliseconds, which is
        // offset-aware — the instant is identical to UtcNow's.
        var now = DateTimeOffset.Now;
        var job = Scheduler.Enqueue(runtime, set, now, userInitiated: true, command.Full);

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

    /// <summary>
    /// Re-reads the named destinations' stored objects and reports what no
    /// longer matches its seal (FR-VER-002, FR-VER-004).
    /// </summary>
    /// <remarks>
    /// The same segment engine the scheduler runs, driven on demand — so the
    /// two cannot disagree about what counts as damage, and a full pass leaves
    /// the sweep's cursor and circuit stamp exactly where a scheduled one
    /// would.
    /// </remarks>
    private async ValueTask<ServiceResult> VerifyDestinationAsync(
        VerifyDestinationCommand command, CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;

        IReadOnlyList<BackupSetConfiguration> sets;
        if (command.BackupSetName is null)
        {
            sets = configuration.BackupSets;
        }
        else if (configuration.FindSet(command.BackupSetName) is { } found)
        {
            sets = [found];
        }
        else
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.BackupSetName}' is configured.");
        }

        if (command.DestinationName is not null && configuration.FindDestination(command.DestinationName) is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No destination named '{command.DestinationName}' is declared.");
        }

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lines = new List<string>();
        var damaged = 0L;
        var matched = false;

        foreach (var set in sets)
        {
            foreach (var reference in set.Destinations)
            {
                if (command.DestinationName is not null
                    && !string.Equals(reference.Ref, command.DestinationName, StringComparison.Ordinal))
                {
                    continue;
                }

                matched = true;
                var declared = configuration.FindDestination(reference.Ref);
                if (command.Probe)
                {
                    // The shallowest depth: can this destination take a backup
                    // at all? Answerable before the first sync, which is the
                    // only moment the deeper settings cannot speak to — there
                    // are no stored bytes to re-read yet.
                    if (declared is null)
                    {
                        lines.Add($"{set.Name} -> {reference.Ref}: no longer declared");
                        damaged++;
                        continue;
                    }

                    var probe = await DestinationProbe
                        .ProbeAsync(runtime, set, declared, now, cancellationToken).ConfigureAwait(false);
                    lines.Add($"{set.Name} -> {reference.Ref}: {probe.Detail}");
                    if (!probe.Viable)
                    {
                        // Counted as damage so the console's exit code carries
                        // it: a probe that found an unusable destination has
                        // failed, whatever it says in prose.
                        damaged++;
                    }

                    continue;
                }

                if (declared is null || !ReplicaSweepJob.Sweeps(declared.Kind))
                {
                    // Said rather than skipped.
                    lines.Add(
                        $"{set.Name} -> {reference.Ref}: not deeply verifiable — "
                        + (declared is null ? "no longer declared" : $"a {declared.Kind} destination is not served yet"));
                    continue;
                }

                // A person asked, so the read goes through no limit. A peer's
                // replica is read over the retrieval session with no cadence
                // needed: a person asking is consent for the read, as a
                // restore is (ADR-0035 Amendment 2).
                var outcome = command.Full
                    ? await ReplicaSweepJob.RunFullAsync(runtime, set, reference.Ref, now, cancellationToken)
                        .ConfigureAwait(false)
                    : await ReplicaSweepJob
                        .SweepAsync(runtime, set, reference.Ref, now, userInitiated: true, cancellationToken)
                        .ConfigureAwait(false);
                if (outcome.Unreadable is { } unreadable && outcome.Examined == 0)
                {
                    // Not damage, and not a pass: nothing was read.
                    lines.Add(
                        $"{set.Name} -> {reference.Ref}: not deeply verifiable now — "
                        + $"its replica could not be read: {unreadable}");
                    continue;
                }

                var (examined, found, repaired, stalledOn, stall) =
                    (outcome.Examined, outcome.Damaged, outcome.Repaired, outcome.StalledOn, outcome.Stall);

                // Found damage is a failed verification whether or not it was
                // repaired: the destination altered what it was given. A read
                // that failed is not damage — nothing was shown altered — so it
                // is said where the sweep stopped, and counted as nothing.
                damaged += found;
                var record = runtime.DestinationSync.Find(set.Id, reference.Ref);
                var stopped = stall is null ? null
                    : stalledOn is null ? $"its replica could not be read: {stall}"
                    : $"blob {stalledOn} could not be read: {stall}";

                // A peer whose operator stated no cadence is read only when a
                // person asks (ADR-0035 Amendment 2), so nothing scheduled
                // carries on from here.
                var swept = ReplicaSweepJob.ScheduledIntervalDays(declared) is not null;

                // What the damage still standing there reaches, by name
                // (FR-VER-005) — all of it, not only what this read found.
                var standing = found > 0 && record?.DamagedKeys is { Count: > 0 } damagedKeys
                    && await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is { } archive
                    ? " — " + DamageReachText.Clause(archive.TraceDamage(damagedKeys))
                    : string.Empty;
                lines.Add(found > 0
                    ? $"{set.Name} -> {reference.Ref}: {found} damaged object(s) of {examined} read — "
                        + (repaired == found
                            ? "each was replaced from a sound copy and re-verified"
                            : $"{repaired} replaced from a sound copy, {found - repaired} with no sound copy to replace them")
                        + standing
                        + (stopped is null ? string.Empty : $"; then {stopped}")
                    : stopped is not null
                        ? $"{set.Name} -> {reference.Ref}: "
                            + (examined == 0 ? $"nothing confirmed — {stopped}" : $"{examined} object(s) confirmed, then {stopped}")
                            + (swept
                                ? "; the sweep tries it again after a pause"
                                : "; nothing sweeps this peer on a schedule, so the next verify-destination tries it again")
                    : $"{set.Name} -> {reference.Ref}: {examined} object(s) confirmed"
                        + (record?.SweepCompletedAt is not null && record.SweepCursor is null
                            ? " — every stored object has now been checked"
                            : swept
                                ? " — more remain; the sweep resumes next pass"
                                : " — more remain; nothing sweeps this peer on a schedule, so the next verify-destination continues from here"));
            }
        }

        if (!matched)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                command.DestinationName is null
                    ? "No backup set declares a destination."
                    : $"No matching set declares destination '{command.DestinationName}'.");
        }

        return new VerifyDestinationResult(lines, damaged);
    }

    /// <summary>
    /// Converges destinations on demand (FR-DEST-002, ADR-0034 §3): one
    /// transfer-lane sync per matching pair, awaited here so the answer
    /// reflects the refreshed ledger. Deliberately NOT on the writer lane —
    /// fan-out reads the set's archive (staging, or a direct-ship set's
    /// replicas through the sink, ADR-0046) and runs on the transfer lane;
    /// a pair whose sync is already queued or running is reported, not
    /// doubled.
    /// </summary>
    private async ValueTask<ServiceResult> SyncAsync(SyncCommand command, CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;

        IReadOnlyList<BackupSetConfiguration> sets;
        if (command.BackupSetName is null)
        {
            sets = configuration.BackupSets;
        }
        else if (configuration.FindSet(command.BackupSetName) is { } found)
        {
            sets = [found];
        }
        else
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.BackupSetName}' is configured.");
        }

        if (command.DestinationName is not null && configuration.FindDestination(command.DestinationName) is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No destination named '{command.DestinationName}' is declared.");
        }

        // Local time for the same reason as the run verb: the offset serves
        // schedule math, and the durable stamps are offset-aware.
        var now = DateTimeOffset.Now;
        var pairs = new List<(BackupSetConfiguration Set, string Destination, Task? Wait)>();
        foreach (var set in sets)
        {
            foreach (var reference in set.Destinations)
            {
                if (command.DestinationName is not null
                    && !string.Equals(reference.Ref, command.DestinationName, StringComparison.Ordinal))
                {
                    continue;
                }

                pairs.Add((set, reference.Ref, FanOut.Enqueue(runtime, set, reference.Ref, now, userInitiated: true)));
            }
        }

        if (pairs.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                command.DestinationName is null
                    ? "No backup set declares a destination."
                    : $"No matching set declares destination '{command.DestinationName}'.");
        }

        foreach (var (set, destination, wait) in pairs)
        {
            if (wait is null)
            {
                continue;
            }

            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A sync that faulted past FanOut's own handlers never wrote
                // its row — and the report below reads the ledger, so an
                // unrecorded fault would replay the LAST outcome as if it
                // were this one's. Record the truth first (FR-DEST-004).
                runtime.DestinationSync.RecordFailure(
                    set.Id, destination, DestinationSyncState.Failed, exception.Message,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        var lines = new List<string>();
        foreach (var (set, destination, wait) in pairs)
        {
            if (wait is null)
            {
                lines.Add($"{set.Name} -> {destination}: already syncing");
                continue;
            }

            lines.Add($"{set.Name} -> {destination}: {DescribeSync(runtime.DestinationSync.Find(set.Id, destination))}");
        }

        return new SyncResult(lines);
    }

    private static string DescribeSync(DestinationSyncRecord? record) => record?.State switch
    {
        null => "nothing to sync yet — no snapshot has been captured",
        DestinationSyncState.InSync => $"in sync ({record.Objects} object(s) copied)",
        DestinationSyncState.Behind => "behind",
        DestinationSyncState.Unavailable => $"unavailable — {record.LastError}",
        DestinationSyncState.Failed => $"failed — {record.LastError}",
        DestinationSyncState.NotSupported => $"not supported — {record.LastError}",
        _ => record.State.ToString(),
    };

    private ServiceResult CancelJob(CancelJobCommand command)
    {
        if (runtime.Queue.Cancel(command.JobId))
        {
            return new AcknowledgedResult();
        }

        // A journal row the queue no longer knows is a run that is not
        // running — a fault outside the runner's catch list orphans one on a
        // live service (ADR-0049). Cancel is the operator's remedy for the
        // stuck card it renders, so it settles the record rather than
        // refusing into a dead end.
        var orphan = runtime.Jobs.Jobs.FirstOrDefault(job => job.Id == command.JobId);
        if (orphan is not null && !Application.JobStateStore.HasSettled(orphan.State))
        {
            runtime.Jobs.Transition(
                orphan.Id, Domain.Jobs.JobState.Cancelled,
                (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                "cancelled by request — the run was no longer live");
            return new AcknowledgedResult();
        }

        return new ServiceError(
            ServiceErrorReason.NotFound,
            $"No job '{command.JobId}' is queued or running. A finished job cannot be cancelled.");
    }

    private JobsResult ListJobs(ListJobsCommand command)
    {
        var jobs = runtime.Jobs.Jobs.AsEnumerable();
        if (command.ActiveOnly)
        {
            jobs = jobs.Where(job => runtime.Queue.IsActive(job.Id));
        }

        if (command.Limit is { } limit && limit > 0)
        {
            // The newest rows, in the same oldest-first order the unbounded
            // form documents — a history view wants the recent past, not the
            // installation's first week.
            jobs = jobs.TakeLast(limit);
        }

        return new JobsResult(
            [.. jobs.Select(job => new JobDescriptor(
                job.Id, job.BackupSetId, job.State, job.StartedAt, job.UpdatedAt, job.SnapshotId, job.Detail,
                job.Stats?.FilesSeen, job.Stats?.FilesDone, job.Stats?.FilesReused, job.Stats?.FilesFailed,
                job.Stats?.BytesSeen, job.Stats?.BytesStored, job.Stats?.TotalFiles, job.Stats?.TotalBytes,
                job.Stats?.BytesBackedUp, job.Stats?.FilesBackedUp))]);
    }

    /// <summary>The failure listing's default and ceiling (ADR-0050): counts stay exact; the listing is bounded well under the frame cap.</summary>
    private const int DefaultFailureSampleLimit = 100;

    private const int MaxFailureSampleLimit = 1000;

    /// <summary>
    /// Resolves a drill-down ask to its journal row and the archive holding
    /// its snapshot. Every refusal is stated: an unknown job, a run that
    /// committed nothing, an archive no set owns any more.
    /// </summary>
    private async ValueTask<(JobRecord? Job, byte[]? SnapshotId, RestoreContext? Context, ServiceError? Error)>
        ResolveJobSnapshotAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = runtime.Jobs.Jobs.FirstOrDefault(row => row.Id == jobId);
        if (job is null)
        {
            return (null, null, null, new ServiceError(
                ServiceErrorReason.NotFound, $"No job '{jobId}' is in the journal."));
        }

        if (job.SnapshotId is null)
        {
            return (job, null, null, new ServiceError(
                ServiceErrorReason.Refused,
                $"Job '{jobId}' committed no snapshot — a {job.State} run has nothing in the repository to report."));
        }

        byte[] snapshotId;
        try
        {
            snapshotId = Convert.FromHexString(job.SnapshotId);
        }
        catch (FormatException)
        {
            return (job, null, null, new ServiceError(
                ServiceErrorReason.NotFound, $"Job '{jobId}' names '{job.SnapshotId}', which is not a snapshot identifier."));
        }

        var (context, _) = await ResolveRestoreContextAsync(
            sourceId: null, snapshotId, job.SnapshotId, cancellationToken).ConfigureAwait(false);
        return context is null
            ? (job, snapshotId, null, new ServiceError(
                ServiceErrorReason.NotFound,
                $"No archive holds snapshot '{job.SnapshotId}' — its set may have been deleted since the run."))
            : (job, snapshotId, context, null);
    }

    private string SetNameOf(string backupSetId) =>
        runtime.Configuration.BackupSets.FirstOrDefault(set => set.Id == backupSetId)?.Name ?? backupSetId;

    /// <summary>An exact count with a bounded, first-encountered sample — the SetChangeScan shape.</summary>
    private sealed class DiffBucket(int limit)
    {
        private readonly List<string> _sample = [];

        public long Count { get; private set; }

        public void Add(string path)
        {
            Count++;
            if (_sample.Count < limit)
            {
                _sample.Add(path);
            }
        }

        public ChangeBucketDescriptor Describe() => new(Count, _sample);
    }

    /// <summary>
    /// The run diff (ADR-0050): the committed snapshot against the set's
    /// previous one, entirely from the catalogue — equal recorded object ids
    /// are the exact "unchanged" (the ListDirectory badges' own rule), so
    /// this agrees with the browser by construction. Reader lane: it walks
    /// two whole leaf listings.
    /// </summary>
    private async ValueTask<ServiceResult> JobChangesAsync(JobChangesCommand command, CancellationToken cancellationToken)
    {
        var (job, snapshotId, context, error) = await ResolveJobSnapshotAsync(command.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (RefuseUnproved(command.Source, command.SessionId, job!.BackupSetId, "A run's changes") is { } unproved)
        {
            return unproved;
        }

        var sampleLimit = Math.Clamp(
            command.SampleLimit ?? SetChangeScan.DefaultSampleLimit, 1, SetChangeScan.MaxSampleLimit);

        using var catalogue = context!.OpenCatalogue();
        var rows = catalogue.EnumerateSnapshots();
        var current = rows.FirstOrDefault(row => row.SnapshotId.Span.SequenceEqual(snapshotId!));
        if (current is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"The catalogue no longer knows snapshot '{job!.SnapshotId}'.");
        }

        // The predecessor is the next same-set row after this one —
        // EnumerateSnapshots is newest first, and this is the same derivation
        // the snapshot browser's change badges use.
        var previous = rows
            .SkipWhile(row => !row.SnapshotId.Span.SequenceEqual(snapshotId!))
            .Skip(1)
            .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(current.BackupSetId.Span));

        cancellationToken.ThrowIfCancellationRequested();

        long unchanged = 0;
        var added = new DiffBucket(sampleLimit);
        var changed = new DiffBucket(sampleLimit);
        var removed = new DiffBucket(sampleLimit);

        var after = catalogue.EnumerateLeaves(snapshotId!);
        if (previous is null)
        {
            foreach (var entry in after)
            {
                added.Add(entry.Path);
            }
        }
        else
        {
            var before = catalogue.EnumerateLeaves(previous.SnapshotId.Span)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);

            foreach (var entry in after)
            {
                if (!before.Remove(entry.Path, out var prior))
                {
                    added.Add(entry.Path);
                }
                else if (prior.ObjectId == entry.ObjectId)
                {
                    unchanged++;
                }
                else
                {
                    changed.Add(entry.Path);
                }
            }

            // What was not claimed by the run's own listing is what the
            // predecessor alone held.
            foreach (var path in before.Keys.Order(StringComparer.Ordinal))
            {
                removed.Add(path);
            }
        }

        return new JobChangesResult(
            SetNameOf(job!.BackupSetId),
            job.SnapshotId!,
            previous is null ? null : Convert.ToHexStringLower(previous.SnapshotId.Span),
            previous?.CapturedAt,
            unchanged,
            added.Describe(),
            changed.Describe(),
            removed.Describe(),
            sampleLimit);
    }

    /// <summary>
    /// The failure listing (ADR-0050): the snapshot's error manifest read
    /// back on demand — path, typed reason, and the scanner's own words.
    /// Paths flow only to a caller who unlocked the run's set with the
    /// passphrase (FR-WOR-007), as the listing's do;
    /// the raw name bytes stay in the manifest and the rendering substitutes
    /// where they have no faithful decoding. Reader lane: it opens the
    /// repository's blob footers to reach two records.
    /// </summary>
    private async ValueTask<ServiceResult> JobFailuresAsync(JobFailuresCommand command, CancellationToken cancellationToken)
    {
        var (job, snapshotId, context, error) = await ResolveJobSnapshotAsync(command.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (RefuseUnproved(command.Source, command.SessionId, job!.BackupSetId, "A run's failures") is { } unproved)
        {
            return unproved;
        }

        var sampleLimit = Math.Clamp(
            command.SampleLimit ?? DefaultFailureSampleLimit, 1, MaxFailureSampleLimit);
        var setName = SetNameOf(job!.BackupSetId);

        Domain.Identifiers.ObjectId recordId;
        using (var catalogue = context!.OpenCatalogue())
        {
            var current = catalogue.EnumerateSnapshots()
                .FirstOrDefault(row => row.SnapshotId.Span.SequenceEqual(snapshotId!));
            if (current is null)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound, $"The catalogue no longer knows snapshot '{job.SnapshotId}'.");
            }

            recordId = current.ObjectId;
        }

        using var reader = new RepositoryReader(context.RepositoryId, context.Keys, context.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var manifestRead = await reader.ReadSegmentAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (manifestRead.Outcome != Repository.Packing.RecordReadOutcome.Ok || manifestRead.Plaintext is null)
        {
            return new ServiceError(
                ServiceErrorReason.Failed,
                $"Snapshot '{job.SnapshotId}' would not read back ({manifestRead.Outcome}) — run `check`.");
        }

        try
        {
            var decoded = Repository.Format.Manifests.SnapshotManifestCodec.Decode(manifestRead.Plaintext);
            if (decoded.Manifest.ErrorManifest is not { } errorManifestId)
            {
                return new JobFailuresResult(setName, job.SnapshotId!, Failures: 0, [], sampleLimit);
            }

            var errorRead = await reader.ReadSegmentAsync(errorManifestId, cancellationToken).ConfigureAwait(false);
            if (errorRead.Outcome != Repository.Packing.RecordReadOutcome.Ok || errorRead.Plaintext is null)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"The error manifest of snapshot '{job.SnapshotId}' would not read back ({errorRead.Outcome}) — run `check`.");
            }

            var manifest = Repository.Format.Manifests.ErrorManifestCodec.Decode(errorRead.Plaintext);
            return new JobFailuresResult(
                setName,
                job.SnapshotId!,
                manifest.Failures.Count,
                [.. manifest.Failures.Take(sampleLimit).Select(failure => new CaptureFailureDescriptor(
                    string.Join('/', failure.PathComponents.Select(component =>
                        System.Text.Encoding.UTF8.GetString(component.Span))),
                    FailureReasonLabel(failure.Reason),
                    failure.Detail))],
                sampleLimit);
        }
        catch (FormatException exception)
        {
            // Includes ManifestValidationException: damage is reported, never
            // rethrown across the wire as a stack trace.
            return new ServiceError(
                ServiceErrorReason.Failed,
                $"Snapshot '{job.SnapshotId}' carries a manifest that does not validate: {exception.Message}");
        }
    }

    /// <summary>The documented kebab vocabulary for <see cref="CaptureFailureDescriptor.Reason"/>.</summary>
    private static string FailureReasonLabel(CaptureFailureReason reason) => reason switch
    {
        CaptureFailureReason.Permission => "permission",
        CaptureFailureReason.NotFound => "not-found",
        CaptureFailureReason.IoError => "io-error",
        CaptureFailureReason.ChangedDuringRead => "changed-during-read",
        CaptureFailureReason.UnsupportedType => "unsupported-type",
        CaptureFailureReason.TooLarge => "too-large",
        CaptureFailureReason.ExcludedByLimit => "excluded-by-limit",
        CaptureFailureReason.NameNotRepresentable => "name-not-representable",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private async ValueTask<ServiceResult> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<SnapshotDescriptor>();
        foreach (var (set, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            // The per-(snapshot, destination) vocabulary (FR-SNP-003) is
            // derived here from the same ledger rows the gate and the status
            // roll-up read — its currency is the snapshot's publication
            // sequence, parsed from the standalone records' cleartext.
            var sequences = await SnapshotSequencesAsync(archive, cancellationToken).ConfigureAwait(false);

            // A snapshot a person asked to delete stays listed until every copy
            // has let it go, and says which copies it waits on (FR-GC-013).
            var requests = await Retention.SnapshotDeletion.ReadRequestsAsync(
                archive.Store, archive.Repository, cancellationToken).ConfigureAwait(false);

            using var catalogue = archive.OpenReadCatalogue();
            var rows = catalogue.EnumerateSnapshots().ToList();

            // Whether each capture time fits its writer's publication order
            // (FR-GC-012): the question retention asks before it expires
            // anything, asked of the same facts, so the listing and the report
            // cannot disagree about which snapshots are flagged.
            var implausible = Retention.RetentionPlanner.FindImplausible(
                    [
                        .. rows.Select(row => new Retention.SnapshotFact(
                            Convert.ToHexStringLower(row.SnapshotId.Span),
                            row.CapturedAt,
                            sequences.TryGetValue(row.ObjectId, out var place) ? place.Sequence : 0,
                            row.CaptureStatus,
                            place.Writer)),
                    ],
                    DateTimeOffset.UtcNow,
                    runtime.Configuration.EffectiveClockSkewMargin)
                .ToDictionary(finding => finding.Snapshot.SnapshotId, finding => finding.Direction, StringComparer.Ordinal);

            // What each destination's outstanding damage reaches, traced once
            // per destination and now rather than when it was found, so a
            // snapshot taken since that needs the same objects is counted too
            // (FR-VER-005).
            var reached = new Dictionary<string, Repository.Catalogue.DamageReach>(StringComparer.Ordinal);
            foreach (var reference in set.Destinations)
            {
                if (runtime.DestinationSync.Find(set.Id, reference.Ref)?.DamagedKeys is { Count: > 0 } damaged)
                {
                    reached[reference.Ref] = TraceOrUntraced(catalogue, archive, damaged);
                }
            }

            foreach (var row in rows)
            {
                List<string>? destinations = null;
                if (sequences.TryGetValue(row.ObjectId, out var published))
                {
                    var sequence = published.Sequence;
                    var snapshotHex = Convert.ToHexStringLower(row.SnapshotId.Span);
                    destinations = [.. set.Destinations.Select(reference => string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{reference.Ref}: {SnapshotReplication.Label(SnapshotReplication.Derive(
                            sequence,
                            runtime.DestinationSync.Find(set.Id, reference.Ref),
                            runtime.Queue.IsActive(FanOut.JobIdFor(set.Id, reference.Ref)),
                            touchesDamage: reached.TryGetValue(reference.Ref, out var reach)
                                && (!reach.Complete || reach.Snapshots.Contains(snapshotHex))))}"))];
                }

                snapshots.Add(new SnapshotDescriptor(
                    Convert.ToHexString(row.SnapshotId.Span).ToLowerInvariant(),
                    Convert.ToHexString(row.BackupSetId.Span).ToLowerInvariant(),
                    row.CapturedAt,
                    row.CaptureStatus,
                    catalogue.CountFiles(row.SnapshotId.Span),
                    destinations,
                    row.ConsistencyMethod,
                    row.ObservedClockSkewMs,
                    implausible.TryGetValue(Convert.ToHexStringLower(row.SnapshotId.Span), out var direction)
                        ? direction == Retention.ImplausibleCaptureTime.Behind ? "behind" : "ahead"
                        : null,
                    requests.TryGetValue(row.ObjectId, out var requested)
                        ? [.. set.Destinations
                            .Select(reference => reference.Ref)
                            .Where(name =>
                                (runtime.DestinationSync.Find(set.Id, name)?.ConvergedSequence ?? 0) < requested)]
                        : null));
            }
        }

        return new SnapshotsResult(snapshots);
    }

    /// <summary>
    /// What damage to <paramref name="damagedKeys"/> reaches through a
    /// catalogue already open, or — when that catalogue cannot be read —
    /// nothing traced, which counts every snapshot as reached.
    /// </summary>
    private static Repository.Catalogue.DamageReach TraceOrUntraced(
        Repository.Catalogue.Catalogue catalogue, ArchiveHandle archive, IReadOnlyList<string> damagedKeys)
    {
        try
        {
            return DamageScope.Trace(catalogue, archive.Repository.Keys, damagedKeys);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or IOException)
        {
            return Repository.Catalogue.DamageReach.None with { Untraced = damagedKeys.Count };
        }
    }

    /// <summary>
    /// Each snapshot object's publication sequence and the writer it belongs
    /// to, keyed by its record object id — read from the standalone framing's
    /// cleartext, the per-publication monotonic the replication ledger also
    /// speaks (FR-GC-009) and one writer's own (FR-GC-012). An unparseable
    /// object simply claims nothing here.
    /// </summary>
    private static async ValueTask<Dictionary<Domain.Identifiers.ObjectId, (ulong Sequence, string Writer)>> SnapshotSequencesAsync(
        ArchiveHandle archive, CancellationToken cancellationToken)
    {
        var sequences = new Dictionary<Domain.Identifiers.ObjectId, (ulong Sequence, string Writer)>();
        await foreach (var entry in archive.Store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("snapshots/"),
            Storage.Abstractions.ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            using var read = await archive.Store.OpenReadAsync(entry.Key, range: null, cancellationToken)
                .ConfigureAwait(false);
            if (read.Outcome != Storage.Abstractions.OpenReadOutcome.Found)
            {
                continue;
            }

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            try
            {
                var record = Repository.Format.Records.StandaloneRecordFraming.Parse(memory.ToArray());
                sequences[record.Header.ObjectId] = (record.Counter, record.WriterId.ToString());
            }
            catch (FormatException)
            {
                // Includes RecordFormatException: an object that does not
                // parse as a standalone record claims no sequence.
            }
        }

        return sequences;
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

        if (RefuseUnproved(command.Source, command.SessionId, setId: null, "Listing a snapshot") is { } unproved)
        {
            return unproved;
        }

        var (context, contextError) = await ResolveRestoreContextAsync(
            command.Source, snapshotId, command.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return command.Source is null
                ? new ServiceError(ServiceErrorReason.NotFound, $"No snapshot '{command.SnapshotId}' exists.")
                : contextError!;
        }

        if (context.Source is { } source)
        {
            await source.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
        using var catalogue = context.OpenCatalogue();
        var path = command.Path ?? string.Empty;
        var entries = catalogue.ListDirectory(snapshotId, path);

        // The set's previous snapshot, when one exists: EnumerateSnapshots is
        // newest first, so the predecessor is the next same-set row after
        // this one. Both listings name a path identically, so the comparison
        // is a dictionary join — two queries, never one per entry.
        var rows = catalogue.EnumerateSnapshots();
        var current = rows.FirstOrDefault(row => row.SnapshotId.Span.SequenceEqual(snapshotId));
        Repository.Catalogue.CatalogueSnapshot? previous = null;
        if (current is not null)
        {
            previous = rows
                .SkipWhile(row => !row.SnapshotId.Span.SequenceEqual(snapshotId))
                .Skip(1)
                .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(current.BackupSetId.Span));
        }

        Dictionary<string, Repository.Catalogue.CatalogueTreeEntry>? before = null;
        if (previous is not null)
        {
            before = catalogue.ListDirectory(previous.SnapshotId.Span, path)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }

        // What the entry's recorded object says: equal ids are the same
        // statement — an unchanged file re-emits its prior manifest's id
        // verbatim, so the comparison is exact. A directory makes no claim:
        // its id is the tree chain head, whose recorded metadata mixes in
        // access times the scan itself perturbs, so a folder-level marker
        // would read "changed" as noise — open the folder and its own
        // entries answer (ADR-0039).
        string? ChangeOf(Repository.Catalogue.CatalogueTreeEntry entry) =>
            before is null || entry.EntryKind == Domain.EntryKind.DirectoryPlaceholder ? null
            : !before.TryGetValue(entry.Path, out var prior) ? "new"
            : prior.ObjectId == entry.ObjectId ? "same"
            : "changed";

        List<string>? deleted = null;
        if (before is not null)
        {
            var present = entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            deleted = [.. before.Keys
                .Where(priorPath => !present.Contains(priorPath))
                .Select(priorPath => priorPath.Split('/')[^1])
                .Order(StringComparer.Ordinal)];
        }

        // The documented vocabulary is file | directory | symlink | special —
        // the enum's own name for a directory row is its internal
        // "placeholder" framing, which leaked to the wire before ADR-0039 and
        // matched no client's check.
        static string KindOf(Domain.EntryKind kind) => kind switch
        {
            Domain.EntryKind.DirectoryPlaceholder => "directory",
            _ => kind.ToString().ToLowerInvariant(),
        };

        return new DirectoryResult(
            path,
            [.. entries.Select(entry => new DirectoryEntryDescriptor(
                entry.Path.Split('/')[^1],
                KindOf(entry.EntryKind),
                (long)(entry.LogicalLength ?? 0),
                entry.ModifiedAt,
                ChangeOf(entry)))],
            deleted,
            previous is null ? null : Convert.ToHexStringLower(previous.SnapshotId.Span));
        }
        finally
        {
            context.Source?.Gate.Release();
        }
    }

    /// <summary>
    /// The notices, structured (FR-DEST-008, ADR-0039): identity for
    /// acknowledgement, key for grouping, raised-at for age — everything the
    /// status strings flatten away. Oldest first, so the longest-waiting
    /// notice reads first.
    /// </summary>
    private NoticesResult ListNotices(ListNoticesCommand command) =>
        new([.. (command.IncludeAcknowledged
                ? runtime.Notices.Notices.OrderBy(notice => notice.RaisedAt)
                : runtime.Notices.Unacknowledged.OrderBy(notice => notice.RaisedAt))
            .Select(notice => new NoticeDescriptor(
                notice.Id, notice.Key, notice.Message, notice.RaisedAt, notice.AcknowledgedAt))]);

    /// <summary>A person has seen the notice; it stays on record (FR-DEST-008).</summary>
    private ServiceResult AcknowledgeNotice(AcknowledgeNoticeCommand command) =>
        runtime.Notices.Acknowledge(command.Id, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ? new AcknowledgedResult()
            : new ServiceError(ServiceErrorReason.NotFound, $"No unacknowledged notice '{command.Id}' exists.");

    private async ValueTask<ServiceResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;

        // One clock read, used for all three answers below. It was two —
        // DateTimeOffset.UtcNow for ObservedAt and a separate .Now for each
        // set's NextRun — and nothing depended on them agreeing until the
        // background window arrived (ADR-0069): a status saying "shut" from
        // one instant and "opens at 06:00" from another can contradict itself
        // across a boundary, at exactly the moment somebody is looking.
        //
        // It is a LOCAL instant on purpose, and the reason is invisible from
        // here: BackgroundWindow.IsOpen reads the wall clock of the offset it
        // is handed, so UtcNow would answer for UTC's clock face and the
        // status would disagree with the pass by this machine's offset —
        // silently, and never on a machine that happens to run UTC. The
        // millisecond stamp is the same number either way; a Unix timestamp
        // names an instant and not a zone.
        var observed = DateTimeOffset.Now;
        var now = (ulong)observed.ToUnixTimeMilliseconds();
        var sets = new List<BackupSetStatusDescriptor>();

        foreach (var set in configuration.BackupSets)
        {
            // Each set answers from its own archive (ADR-0034). A set never
            // backed up has no archive: no snapshot, no findings — Unprotected
            // by honest absence rather than by error.
            Repository.Catalogue.CatalogueSnapshot? latest = null;
            var findings = 0;
            var files = new Dictionary<string, FilesFigure>(StringComparer.Ordinal);
            if (await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is { } archive)
            {
                using var catalogue = archive.OpenReadCatalogue();
                var setId = Convert.FromHexString(set.Id);

                // Newest capture first, so the first of this set's is the one
                // a status describes.
                latest = catalogue.EnumerateSnapshots()
                    .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));
                findings = catalogue.Findings().Count;

                var writing = archive.ShipSink?.DestinationsThisRun ?? [];
                foreach (var reference in set.Destinations)
                {
                    files[reference.Ref] = FilesFigureOf(set, reference.Ref, catalogue, latest, writing);
                }
            }

            var (inputs, rows, lastCompleted) = DescribeDestinations(configuration, set, latest is not null, files);
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
                var anchor = runtime.Jobs.ScheduleAnchor(set.Id);
                nextRun = schedule!.NextRun(anchor, observed).ToString("u");
            }

            sets.Add(new BackupSetStatusDescriptor(
                set.Name, status, nextRun, rows,
                LastCompletedAt: lastCompleted == 0 ? null : lastCompleted));
        }

        // From the parsed window, never from the presence of the text: a window
        // that is SET is not a window that is SHUT, and reading the string's
        // presence as the state is the mistake that looks right on a fixture
        // and wrong every morning.
        BackgroundWindowDescriptor? window = null;
        if (configuration.EffectiveBackgroundWindow is { } configured)
        {
            var open = configured.IsOpen(observed);
            var changes = open ? configured.NextClose(observed) : configured.NextOpen(observed);
            window = new BackgroundWindowDescriptor(
                configured.Text, open, (ulong)changes.ToUnixTimeMilliseconds());
        }

        // The byte-rate limits in force (contract 1.43, ADR-0074), from the
        // parsed limits as the window is from the parsed window. Null rather
        // than an empty descriptor when nothing is limited: a client draws no
        // line either way, and null is what an older service says too.
        BackgroundLimitsDescriptor? limits = null;
        var readLimit = configuration.EffectiveBackgroundReadLimit;
        List<DestinationTransferLimitDescriptor> transferLimits =
        [
            .. configuration.Destinations
                .Select(destination => (destination.Name, Rate: destination.EffectiveTransferLimit))
                .Where(limited => limited.Rate is not null)
                .Select(limited => new DestinationTransferLimitDescriptor(
                    limited.Name, limited.Rate!.Text, limited.Rate.BytesPerSecond)),
        ];
        if (readLimit is not null || transferLimits.Count > 0)
        {
            limits = new BackgroundLimitsDescriptor(
                readLimit is null ? null : new ByteRateDescriptor(readLimit.Text, readLimit.BytesPerSecond),
                transferLimits);
        }

        return new StatusResult(
            Environment.MachineName, sets, now,
            [.. runtime.Notices.Unacknowledged.Select(notice => $"[{notice.Id}] {notice.Message}")],
            window,
            limits);
    }

    /// <summary>
    /// One set's destination matrix, twice over: the derivation's inputs and
    /// the client's rows, built together so they cannot disagree.
    /// </summary>
    private (IReadOnlyList<DestinationStatusInput> Inputs, IReadOnlyList<DestinationStatusDescriptor> Rows, ulong LastCompleted)
        DescribeDestinations(
            ClientConfiguration configuration, BackupSetConfiguration set, bool hasSnapshot,
            IReadOnlyDictionary<string, FilesFigure>? files = null)
    {
        var lastCompleted = runtime.Jobs.LastCompleted(set.Id)?.UpdatedAt ?? 0;
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inputs = new List<DestinationStatusInput>();
        var rows = new List<DestinationStatusDescriptor>();

        foreach (var reference in set.Destinations)
        {
            var destination = configuration.FindDestination(reference.Ref);
            var ledger = runtime.DestinationSync.Find(set.Id, reference.Ref);
            var input = DestinationStatus.Describe(
                reference.Ref, destination, [.. set.Roots.Select(root => root.Path)],
                ledger, lastCompleted, nowMs, runtime.VolumeIdOf, hasSnapshot);

            inputs.Add(input);
            var figure = files?.GetValueOrDefault(reference.Ref) ?? default;
            rows.Add(new DestinationStatusDescriptor(
                input.Name, destination is null ? "?" : KindLabel(input.Kind), StateLabel(input.Sync),
                input.LastSuccessAt, input.Detail, StatusDeriver.DomainLabel(input.Domain),
                StatusDeriver.VerificationLabel(input),
                BaselineCompletedAt: ledger?.BaselineCompletedAt,
                NeedsFull: ledger?.NeedsFull ?? false,
                Reason: ReasonLabel(input.Cause),
                HeldBytes: ledger?.HeldBytes ?? 0,
                OwedBytes: ledger?.OwedBytes ?? 0,
                MeasuredAt: ledger?.MeasuredAt,
                DrilledAt: ledger?.DrilledAt,
                DrillFiles: ledger?.DrillFiles ?? 0,
                DrillFailure: ledger?.DrillFailure,
                DrillLimit: ledger?.DrillLimit,
                VerifiedSealed: ledger?.VerifiedSealed ?? 0,
                VerifiedDigest: ledger?.VerifiedDigest ?? 0,
                VerifiedChunk: ledger?.VerifiedChunk ?? 0,
                DeepSweep: DescribeSweep(destination, ledger),
                FilesHeld: figure.Held,
                FilesTotal: figure.Total,
                HoldsNewest: figure.Whole,
                InRun: figure.InRun,
                Syncing: figure.Syncing));
        }

        return (inputs, rows, lastCompleted);
    }

    /// <summary>
    /// What one destination holds of the set's newest backup (ADR-0088
    /// Amendment 1): a sync's own live count while one is filling it,
    /// otherwise worked out from the ledger's watermark, and not counted at
    /// all where nothing has ever been delivered.
    /// </summary>
    private FilesFigure FilesFigureOf(
        BackupSetConfiguration set, string destination, Repository.Catalogue.Catalogue catalogue,
        Repository.Catalogue.CatalogueSnapshot? newest, IReadOnlyCollection<string> writing)
    {
        var inRun = writing.Contains(destination, StringComparer.Ordinal);
        if (runtime.Holdings.Find(set.Id, destination) is { } live)
        {
            return new FilesFigure(live.Held, live.Total, Whole: false, inRun, Syncing: true);
        }

        if (newest is null)
        {
            return new FilesFigure(null, null, Whole: false, inRun, Syncing: false);
        }

        var synced = runtime.DestinationSync.Find(set.Id, destination)?.SyncedSequence ?? 0;
        if (synced == 0)
        {
            return new FilesFigure(null, catalogue.CountFiles(newest.SnapshotId.Span), Whole: false, inRun, Syncing: false);
        }

        var delivered = runtime.Delivered.Get(
            set.Id, destination, Convert.ToHexStringLower(newest.SnapshotId.Span), synced,
            () => catalogue.FilesDeliveredThrough(newest.SnapshotId.Span, runtime.Writer, synced));
        return new FilesFigure(delivered.Held, delivered.Total, delivered.Whole, inRun, Syncing: false);
    }

    /// <summary>One destination's files of the newest backup, as its status row carries them.</summary>
    private readonly record struct FilesFigure(long? Held, long? Total, bool Whole, bool InRun, bool Syncing);

    /// <summary>
    /// A destination's deep sweep for its status row (contract 1.46, ADR-0035
    /// Amendment 3): the ledger's facts, and the cadence the scheduler keeps.
    /// </summary>
    /// <returns>
    /// Null where there is no sweep to report — a kind nothing reads back in
    /// full, or a destination no longer declared. A sweep that has not run is
    /// a descriptor with nothing closed, never a null.
    /// </returns>
    internal static DeepSweepDescriptor? DescribeSweep(DestinationConfiguration? destination, DestinationSyncRecord? ledger) =>
        destination is null || !ReplicaSweepJob.Sweeps(destination.Kind)
            ? null
            : new DeepSweepDescriptor(
                ReplicaSweepJob.ScheduledIntervalDays(destination),
                ledger?.SweepCompletedAt,
                ledger?.SweptThisCircuit ?? 0,
                ledger?.SweptAt,
                ledger?.SweepStalls ?? 0,
                ledger?.SweepStalledOn);

    /// <summary>The documented kebab vocabulary for <see cref="DestinationStatusDescriptor.Reason"/>; null when there is nothing to explain.</summary>
    private static string? ReasonLabel(SyncCause cause) => cause switch
    {
        SyncCause.CatchingUp => "catching-up",
        SyncCause.AwaitingSeed => "awaiting-seed",
        SyncCause.NeverSynced => "never-synced",
        SyncCause.Reported => "reported",
        SyncCause.AwaitingFirstBackup => "awaiting-first-backup",
        _ => null,
    };

    /// <summary>
    /// The destination's failure domain (FR-SNP-007): the declaration wins —
    /// only the user knows where the NAS actually sits (ADR-0018) — and the
    /// default is derived by kind. A local path is compared by device
    /// identity, staying conservative (same-volume) when the platform cannot
    /// say, and never inferring past same-machine: a second disk still dies
    /// with the machine. A peer defaults to same-site — a LAN friend does
    /// not survive the house fire — and a cloud kind to independent
    /// (ADR-0018 Amendment 2).
    /// </summary>
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

    private ServiceDescriptionResult Describe()
    {
        // The public half of the installation's derivation (contract 1.28):
        // the same three facts every archive's descriptor records, plus the
        // verifier, so a client holding the passphrase can derive a grant
        // without holding an archive. Nothing here opens anything.
        using var provisioning = runtime.InstallationCredential.TryLoad();

        return new ServiceDescriptionResult(
            ContractVersion.Current.ToString(),
            ServiceVersion,
            Environment.MachineName,
            runtime.Options.StateDirectory,
            remoteBinding.Enabled,
            runtime.Queue.ActiveCount,
            runtime.Options.ArchivesRoot,
            runtime.GrantRecipient.PublicKeyHex,
            runtime.SetupState,
            Convert.ToHexStringLower(runtime.State.DeviceId),
            runtime.Options.Logging is { } logging
                ? Domain.Diagnostics.LogLevels.NameOf(logging.Levels.Current.Default)
                : null,
            KdfSalt: provisioning is null ? null : Convert.ToHexStringLower(provisioning.KdfSalt),
            KdfMemoryKib: provisioning?.KdfParameters.MemoryKiB,
            KdfIterations: provisioning?.KdfParameters.Iterations,
            KdfParallelism: provisioning?.KdfParameters.Parallelism,
            SealingPublicKey: provisioning is null
                ? null
                : Convert.ToHexStringLower(provisioning.Credential.SealingPublicKey));
    }
}

/// <summary>
/// Where the caller on a session arrived from (ADR-0044 §5).
/// </summary>
/// <remarks>
/// The listener that owns the session knows this and nothing else does: the
/// remote listener presents <see cref="Remote"/>, the local one
/// <see cref="Local"/>. It exists because refusing a verb to a remote console
/// is a per-caller question, and the only fact the handler had was whether
/// the remote binding was listening at all.
/// </remarks>
public enum CallerScope
{
    /// <summary>Over the local binding — a Unix socket or named pipe on this machine.</summary>
    Local,

    /// <summary>Over the remote binding — a paired device elsewhere (ADR-0028 §6).</summary>
    Remote,

    /// <summary>
    /// The service's own work — a recovery drill — with no person behind it.
    /// It reads the structure plane on the write bundle alone, as backup and
    /// retention do (FR-WOR-003), so the passphrase gate does not apply to it
    /// (FR-WOR-007); no listener ever builds a handler in this scope.
    /// </summary>
    Service,

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
