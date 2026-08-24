using Bodu;
using System.Diagnostics;
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
/// (NFR-SEC-009). Exporting a recovery kit is <b>not a command</b> — it
/// re-derives the key-encryption key from a passphrase supplied per invocation,
/// so it runs where the person typed it.
/// </para>
/// </remarks>
public sealed partial class ServiceCommandHandler(
    ServiceRuntime runtime, RemoteBindingState remoteBinding, CallerScope scope = CallerScope.Local)
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
            Log.CommandExecuted(log, command.GetType().Name, answer.GetType().Name,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
            // The set gate (ADR-0029 Amendment 2): the destructive half must
            // not run while a sync for this set is mid-flight — the two can
            // otherwise conspire to delete a trimmed blob's last copy. A sync
            // can run for hours, and the writer lane must never stall behind
            // one, so retention TRIES: unavailable means this set reports
            // only, and the deferral is named.
            SemaphoreSlim? acquiredGate = null;
            var apply = command.Apply;
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
                    runtime.LoggerFor(typeof(Retention.RetentionRunner))).ConfigureAwait(false);
            }
            finally
            {
                acquiredGate?.Release();
            }

            lines.AddRange(report.Lines.Select(line => $"{set.Name}: {line}"));
            if (command.Apply && !apply)
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
        }

        return new RetentionResult(lines);
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
                        StoreComposition.OpenLocal(replicaRoot))
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
        UpsertBackupSetCommand upsert => UpsertBackupSet(upsert),
        DeleteBackupSetCommand deleteSet => DeleteBackupSet(deleteSet),
        ListDestinationsCommand => ListDestinations(),
        UpsertDestinationCommand upsertDestination => UpsertDestination(upsertDestination),
        DeleteDestinationCommand deleteDestination => DeleteDestination(deleteDestination),
        ListPairingsCommand => ListPairings(),
        GetDiagnosticsCommand => GetDiagnostics(),
        SetLogLevelCommand setLevel => SetLogLevel(setLevel),
        ReadLogCommand readLog => ReadLog(readLog),
        BrowseFoldersCommand browse => BrowseFolders(browse),
        ValidateSetDraftCommand draft => ValidateSetDraft(draft),
        CreatePairingInviteCommand invite => CreatePairingInvite(invite),
        ListPairingInvitesCommand => ListPairingInvites(),
        RevokePairingInviteCommand revoke => RevokePairingInvite(revoke),
        PairWithInviteCommand pair => await PairWithInviteAsync(pair, cancellationToken).ConfigureAwait(false),
        ListNoticesCommand listNotices => ListNotices(listNotices),
        AcknowledgeNoticeCommand acknowledge => AcknowledgeNotice(acknowledge),
        UnpairCommand unpair => await UnpairAsync(unpair, cancellationToken).ConfigureAwait(false),
        ClaimReplicasCommand claim => await ClaimReplicasAsync(claim, cancellationToken).ConfigureAwait(false),
        RunBackupCommand run => RunBackup(run),
        CancelJobCommand cancel => CancelJob(cancel),
        ListJobsCommand list => ListJobs(list),
        ListSnapshotsCommand => await ListSnapshotsAsync(cancellationToken).ConfigureAwait(false),
        ListDirectoryCommand list => await ListDirectoryAsync(list, cancellationToken).ConfigureAwait(false),
        CloseRestoreSourceCommand close => await CloseRestoreSourceAsync(close).ConfigureAwait(false),
        ProvisionWriteOnlySetCommand provision =>
            await ProvisionWriteOnlySetAsync(provision, cancellationToken).ConfigureAwait(false),
        ProvisionInstallationCommand setup => ProvisionInstallation(setup),
        ConfirmRecoveryKitCommand confirm => ConfirmRecoveryKit(confirm),
        SyncCommand sync => await SyncAsync(sync, cancellationToken).ConfigureAwait(false),
        VerifyDestinationCommand deep =>
            await VerifyDestinationAsync(deep, cancellationToken).ConfigureAwait(false),
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
            var plan = RestorePlanner.Plan(
                catalogue, snapshotId, PrefixesOf(command.Paths, command.Path),
                RestoreTargetProfile.ForLocalPlatform());

            if (plan.Items.Count == 0)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound,
                    $"The catalogue knows nothing under snapshot {command.SnapshotId}"
                    + $"{(command.Path is { Length: > 0 } path ? $" at '{path}'" : string.Empty)}.");
            }

            var (missing, _) = await ProbePlanAsync(context, catalogue, plan, cancellationToken).ConfigureAwait(false);
            return new RestorePlanResult(
                plan.Items.Count(item => item.Kind != EntryKind.DirectoryPlaceholder),
                (long)plan.SpaceEstimateBytes,
                missing,
                plan.Conflicts.Count,
                plan.Conflicts.Count == 0
                    ? null
                    : [.. plan.Conflicts.Take(20).Select(conflict => $"{conflict.Path} — {conflict.Reason}")],
                plan.Degradations.Count == 0
                    ? null
                    : [.. plan.Degradations.Select(degradation => degradation.Detail)]);
        }
        finally
        {
            context.Source?.Gate.Release();
        }
    }

    /// <summary>
    /// What a plan is for: the objects it needs and cannot find, reported
    /// before any byte moves rather than discovered part-way through. The
    /// catalogue alone cannot answer this — it is a cache, and a cache
    /// ahead of the store says "nothing missing" about the very objects
    /// the store has lost — so each located blob is probed against the
    /// store, one memoized metadata call per distinct blob. The manifest
    /// blob alone is not enough: an item's SEGMENTS live in other blobs —
    /// after a staging trim, precisely the ones no longer here (ADR-0034
    /// §6) — so each manifest is read (metadata never trims) and its
    /// referenced blobs are probed too (FR-RST-003). The keys that answered
    /// are returned too: they are exactly the blob set a run needs, which is
    /// what the targeted load opens instead of every footer in the store.
    /// </summary>
    private static async ValueTask<(List<string> Missing, HashSet<ObjectKey> NeededBlobs)> ProbePlanAsync(
        RestoreContext context,
        Repository.Catalogue.Catalogue catalogue,
        RestorePlan plan,
        CancellationToken cancellationToken)
    {
        using var keyDeriver = new Repository.Crypto.StoreBlobKeyDeriver(context.Keys.KeyIdKey);
        using var objectIdDeriver = new Repository.Crypto.ObjectIdDeriver(context.Keys.ContentIdKey);
        using var metaReaders = new MetaReaderCache();
        var blobKeys = new Dictionary<Domain.Identifiers.BlobId, ObjectKey?>();
        var missing = new List<string>();
        var needed = new HashSet<ObjectKey>();

        async ValueTask<bool> PresentAsync(Repository.Catalogue.ResolvedLocation location)
        {
            if (!blobKeys.TryGetValue(location.BlobId, out var key))
            {
                key = await FindBlobKeyAsync(
                    context.Store, location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId), cancellationToken)
                    .ConfigureAwait(false);
                blobKeys[location.BlobId] = key;
            }

            if (key is { } found)
            {
                needed.Add(found);
                return true;
            }

            return false;
        }

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

            if (!await PresentAsync(location).ConfigureAwait(false))
            {
                missing.Add(item.Path);
                continue;
            }

            // A manifest that is present but will not read is damage, not
            // absence — verify's business, and nothing this plan can name
            // segments from.
            var manifest = await ReadManifestAsync(
                context, item.ObjectId, location, keyDeriver, objectIdDeriver, metaReaders, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var references = manifest.SegmentReferences.Select(reference => reference.ObjectId)
                .Concat(manifest.Metadata.AlternateStreams.Select(stream => stream.ObjectId));
            foreach (var referenced in references)
            {
                if (catalogue.ResolveLocation(referenced) is not { } segmentLocation
                    || !await PresentAsync(segmentLocation).ConfigureAwait(false))
                {
                    missing.Add(item.Path);
                    break;
                }
            }
        }

        return (missing, needed);
    }

    /// <summary>
    /// Reads one file-version manifest through its meta blob's authenticated
    /// footer — the plan-side targeted read, cached per blob because a
    /// snapshot's manifests cluster in a few metadata blobs.
    /// </summary>
    private static async ValueTask<Repository.Format.Manifests.FileVersionManifest?> ReadManifestAsync(
        RestoreContext context,
        Domain.Identifiers.ObjectId objectId,
        Repository.Catalogue.ResolvedLocation location,
        Repository.Crypto.StoreBlobKeyDeriver keyDeriver,
        Repository.Crypto.ObjectIdDeriver objectIdDeriver,
        MetaReaderCache metaReaders,
        CancellationToken cancellationToken)
    {
        var storeKey = Repository.Packing.BlobStoreKeys.ForBlob(
            BlobClass.Metadata, location.StoreBlobKey ?? keyDeriver.Derive(location.BlobId));

        if (!metaReaders.TryGet(storeKey, out var cached))
        {
            Repository.Packing.BlobReader? reader;
            try
            {
                var metadata = await context.Store.GetMetadataAsync(storeKey, cancellationToken).ConfigureAwait(false);
                reader = metadata.Metadata is not { Length: > 0 }
                    ? null
                    : await Repository.Packing.BlobReader.OpenAsync(
                        context.Store, storeKey, metadata.Metadata.Length, context.RepositoryId,
                        context.Keys.DeriveClassKey, objectIdDeriver, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Repository.Packing.BlobFormatException or IOException)
            {
                reader = null;
            }

            cached = metaReaders.Add(storeKey, reader);
        }

        if (cached is null || !cached.Manifests.TryGetValue(objectId, out var located))
        {
            return null;
        }

        var read = await cached.Reader.ReadRecordAsync(located, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != Repository.Packing.RecordReadOutcome.Ok || read.Plaintext is null)
        {
            return null;
        }

        try
        {
            return Repository.Format.Manifests.FileVersionManifestCodec.Decode(read.Plaintext);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The plan probe's open meta-blob readers: memoized, because a
    /// snapshot's manifests cluster in a few metadata blobs — and BOUNDED,
    /// because a whole-snapshot plan can touch as many metadata blobs as the
    /// repository holds, and plan memory must not scale with repository size
    /// (NFR-PERF-001). Each reader carries an object-id index over its
    /// manifest records, so the per-item lookup is a dictionary hit rather
    /// than a scan of the blob's whole record table.
    /// </summary>
    private sealed class MetaReaderCache : IDisposable
    {
        private const int Capacity = 32;

        private readonly Dictionary<ObjectKey, Cached?> _entries = [];
        private readonly Queue<ObjectKey> _openOrder = new();

        /// <summary>One open reader and its manifest index.</summary>
        public sealed record Cached(
            Repository.Packing.BlobReader Reader,
            IReadOnlyDictionary<Domain.Identifiers.ObjectId, Repository.Packing.RecordTableEntry> Manifests);

        /// <summary>Looks a blob up; a null <paramref name="cached"/> with a true return is a remembered unreadable blob.</summary>
        public bool TryGet(ObjectKey key, out Cached? cached) => _entries.TryGetValue(key, out cached);

        /// <summary>Caches a freshly opened reader (or the fact that the blob would not open).</summary>
        public Cached? Add(ObjectKey key, Repository.Packing.BlobReader? reader)
        {
            if (reader is null)
            {
                // Negative entries are a key and a null — never worth evicting.
                _entries[key] = null;
                return null;
            }

            if (_openOrder.Count >= Capacity)
            {
                var evicted = _openOrder.Dequeue();
                if (_entries.Remove(evicted, out var old))
                {
                    old?.Reader.Dispose();
                }
            }

            var manifests = new Dictionary<Domain.Identifiers.ObjectId, Repository.Packing.RecordTableEntry>();
            foreach (var entry in reader.RecordTable)
            {
                if (entry.ObjectType == Domain.ObjectType.FileVersionManifest)
                {
                    manifests.TryAdd(entry.ObjectId, entry);
                }
            }

            var cached = new Cached(reader, manifests);
            _entries[key] = cached;
            _openOrder.Enqueue(key);
            return cached;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var cached in _entries.Values)
            {
                cached?.Reader.Dispose();
            }

            _entries.Clear();
        }
    }

    /// <summary>
    /// The store key a blob actually exists under, trying both classes — or
    /// null. A store that cannot answer reads as missing: the plan's job is
    /// to warn before bytes move, and "unreachable" warrants the warning as
    /// much as "absent". The found key is what the run's targeted load opens.
    /// </summary>
    private static async ValueTask<ObjectKey?> FindBlobKeyAsync(
        Storage.Abstractions.IObjectStore store,
        Domain.Identifiers.StoreBlobKey blobKey,
        CancellationToken cancellationToken)
    {
        foreach (var blobClass in new[] { BlobClass.Metadata, BlobClass.Data })
        {
            var key = Repository.Packing.BlobStoreKeys.ForBlob(blobClass, blobKey);
            try
            {
                var metadata = await store.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
                if (metadata.Found && metadata.Metadata!.Length > 0)
                {
                    return key;
                }
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    /// <summary>Performs a restore, writing on this machine (ADR-0028 §6).</summary>
    private async ValueTask<ServiceResult> RunRestoreAsync(RunRestoreCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseSnapshotId(command.SnapshotId, out var snapshotId, out var invalid))
        {
            return invalid!;
        }

        var toOriginal = command.Target == "original";
        if (command.Target is not (null or "folder" or "original"))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, $"'{command.Target}' is not a restore target (folder | original).");
        }

        if (!toOriginal && string.IsNullOrWhiteSpace(command.OutputDirectory))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "A restore needs an output directory.");
        }

        ExistingDestinationPolicy existing;
        switch (command.Existing)
        {
            case null:
                existing = ExistingDestinationPolicy.Preserve;
                break;
            case "rename":
                existing = ExistingDestinationPolicy.WriteBeside;
                break;
            case "overwrite":
                existing = ExistingDestinationPolicy.Replace;
                break;
            default:
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{command.Existing}' is not an existing-file policy (rename | overwrite).");
        }

        var target = RestoreTargetProfile.ForLocalPlatform();
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
            IReadOnlyList<(RestorePlan Plan, string OutputDirectory, string LabelPrefix)> slices;
            if (toOriginal)
            {
                var (resolved, refusal) = SliceForOriginal(catalogue, snapshotId, plan);
                if (resolved is null)
                {
                    return refusal!;
                }

                slices = resolved;
            }
            else
            {
                slices = [(plan, command.OutputDirectory, string.Empty)];
            }

            using var reader = new RepositoryReader(
                context.RepositoryId, context.Keys, context.Store, context.Source?.ReadAuthority);
            if (context.Source is null)
            {
                await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A source may be remote: open only the blobs the plan needs,
                // known exactly from the same probe the plan verb runs
                // (ADR-0041).
                var (_, needed) = await ProbePlanAsync(context, catalogue, plan, cancellationToken).ConfigureAwait(false);
                await reader.LoadBlobsAsync(needed, cancellationToken).ConfigureAwait(false);
            }

            var options = new RestoreExecutionOptions
            {
                DestinationMode = command.InPlace || toOriginal
                    ? RestoreDestinationMode.InPlace
                    : RestoreDestinationMode.Quarantine,
                ExistingDestination = existing,

                // A fresh identifier per run, not per snapshot: two restores of
                // one snapshot must displace into distinct stores, or the second
                // overwrites the first's displaced copies — the single shared
                // refuge architecture 08 §3.1 forbids. A snapshot-derived id
                // made every restore of a snapshot share one.
                RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

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
                    : [.. failures.Take(20).Select(item => $"{item.Path} — {item.Detail}")]);
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
                set.Id, set.Name,
                // Root carries the first root for pre-1.10 clients; Roots is
                // the whole truth (ADR-0040).
                set.Roots[0].Path,
                set.Schedule, set.IncludeRules, set.ExcludeRules,
                [.. set.Destinations.Select(reference => reference.Ref)],
                ToPolicyDescriptor(set.Retention),
                ToOverrideDescriptors(set.Destinations),
                [.. set.Roots.Select(root => new BackupRootDescriptor(root.Path, root.Label))]))]);

    private ServiceResult UpsertBackupSet(UpsertBackupSetCommand command)
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
            Destinations = [.. command.Set.Destinations.Select(name => new SetDestinationReference
            {
                Ref = name,
                Retention = command.Set.DestinationRetention is { } overrides
                    // A carried map is the complete truth: named entries set
                    // (empty clears), unnamed destinations carry no override.
                    ? ToRetention(overrides.GetValueOrDefault(name), existing: null)
                    // No map at all preserves whatever the set held, by name.
                    : existing?.Destinations.FirstOrDefault(reference =>
                        string.Equals(reference.Ref, name, StringComparison.Ordinal))?.Retention,
            })],
        };

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

        // A material edit — the root or the rules — changes what the next
        // snapshot will hold, so it is answered with what changed and, when
        // there is a last backup to compare with, a rescan is queued whose
        // finding stands as a notice until that next backup completes
        // (ADR-0038). Schedule and retention edits change when and how long,
        // not what, and stay a plain acknowledgement.
        if (existing is not null && IsMaterialChange(existing, replacement))
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

            return new ConfigurationChangeResult(lines);
        }

        return new AcknowledgedResult();
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
    /// Converges destinations on demand (FR-DEST-002, ADR-0034 §3): one
    /// transfer-lane sync per matching pair, awaited here so the answer
    /// reflects the refreshed ledger. Deliberately NOT on the writer lane —
    /// fan-out reads staging and runs on the transfer lane; a pair whose
    /// sync is already queued or running is reported, not doubled.
    /// </summary>
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

                if (declared is null || declared.Kind != DestinationKind.LocalPath)
                {
                    // Said rather than skipped: a peer replica lives behind the
                    // wire with no store to read, so its bytes are confirmed by
                    // the range challenge at sync time, not by re-reading here.
                    lines.Add(
                        $"{set.Name} -> {reference.Ref}: not deeply verifiable — "
                        + "only a local path can have its stored bytes re-read");
                    continue;
                }

                var (examined, found) = command.Full
                    ? await ReplicaSweepJob.RunFullAsync(runtime, set, reference.Ref, now, cancellationToken)
                        .ConfigureAwait(false)
                    : await Segment(set, reference.Ref).ConfigureAwait(false);

                damaged += found;
                var record = runtime.DestinationSync.Find(set.Id, reference.Ref);
                lines.Add(found > 0
                    ? $"{set.Name} -> {reference.Ref}: {found} damaged object(s) of {examined} read"
                    : $"{set.Name} -> {reference.Ref}: {examined} object(s) confirmed"
                        + (record?.SweepCompletedAt is not null && record.SweepCursor is null
                            ? " — every stored object has now been checked"
                            : " — more remain; the sweep resumes next pass"));
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

        async Task<(int Examined, int Damaged)> Segment(BackupSetConfiguration set, string destination)
        {
            var outcome = await ReplicaSweepJob
                .RunAsync(runtime, set, destination, now, cancellationToken).ConfigureAwait(false);
            return (outcome.Examined, outcome.Damaged);
        }
    }

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
        foreach (var (set, archive) in await runtime.ExistingArchivesAsync(cancellationToken).ConfigureAwait(false))
        {
            // The per-(snapshot, destination) vocabulary (FR-SNP-003) is
            // derived here from the same ledger rows the gate and the status
            // roll-up read — its currency is the snapshot's publication
            // sequence, parsed from the standalone records' cleartext.
            var sequences = await SnapshotSequencesAsync(archive, cancellationToken).ConfigureAwait(false);

            using var catalogue = archive.OpenReadCatalogue();
            foreach (var row in catalogue.EnumerateSnapshots())
            {
                List<string>? destinations = null;
                if (sequences.TryGetValue(row.ObjectId, out var sequence))
                {
                    destinations = [.. set.Destinations.Select(reference => string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{reference.Ref}: {SnapshotReplication.Label(SnapshotReplication.Derive(
                            sequence,
                            runtime.DestinationSync.Find(set.Id, reference.Ref),
                            runtime.Queue.IsActive(FanOut.JobIdFor(set.Id, reference.Ref))))}"))];
                }

                snapshots.Add(new SnapshotDescriptor(
                    Convert.ToHexString(row.SnapshotId.Span).ToLowerInvariant(),
                    Convert.ToHexString(row.BackupSetId.Span).ToLowerInvariant(),
                    row.CapturedAt,
                    row.CaptureStatus,
                    catalogue.CountFiles(row.SnapshotId.Span),
                    destinations,
                    row.ConsistencyMethod));
            }
        }

        return new SnapshotsResult(snapshots);
    }

    /// <summary>
    /// Each snapshot object's publication sequence, keyed by its record
    /// object id — read from the standalone framing's cleartext counter, the
    /// per-publication monotonic the replication ledger also speaks
    /// (FR-GC-009). An unparseable object simply claims nothing here.
    /// </summary>
    private static async ValueTask<Dictionary<Domain.Identifiers.ObjectId, ulong>> SnapshotSequencesAsync(
        ArchiveHandle archive, CancellationToken cancellationToken)
    {
        var sequences = new Dictionary<Domain.Identifiers.ObjectId, ulong>();
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
                sequences[record.Header.ObjectId] = record.Counter;
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
                var anchor = runtime.Jobs.ScheduleAnchor(set.Id);
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
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inputs = new List<DestinationStatusInput>();
        var rows = new List<DestinationStatusDescriptor>();

        foreach (var reference in set.Destinations)
        {
            var destination = configuration.FindDestination(reference.Ref);
            var input = DestinationStatus.Describe(
                reference.Ref, destination, [.. set.Roots.Select(root => root.Path)],
                runtime.DestinationSync.Find(set.Id, reference.Ref),
                lastCompleted, nowMs, DeviceIdOf);

            inputs.Add(input);
            rows.Add(new DestinationStatusDescriptor(
                input.Name, destination is null ? "?" : KindLabel(input.Kind), StateLabel(input.Sync),
                input.LastSuccessAt, input.Detail, StatusDeriver.DomainLabel(input.Domain),
                StatusDeriver.VerificationLabel(input)));
        }

        return (inputs, rows);
    }

    /// <summary>
    /// The volume a path sits on, or null when the platform will not say —
    /// the one piece of <see cref="DestinationStatus.Describe"/> that has to
    /// be supplied from outside the use-case layer (architecture 11 §2).
    /// </summary>
    private static ulong? DeviceIdOf(string path) => DeviceProbe.DeviceOf(path);

    /// <summary>
    /// The seam is <see cref="Filesystem.IFileSystemSource.DeviceOf"/> being
    /// an interface member — a test double answers it like any other
    /// filesystem question. The field's own type is concrete because a
    /// private field assigned exactly one implementation gains nothing from
    /// interface typing, and the analyzer (CA1859) is right to say so.
    /// </summary>
    private static readonly Filesystem.Local.LocalFileSystemSource DeviceProbe = new();

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

    private ServiceDescriptionResult Describe() =>
        new ServiceDescriptionResult(
            ContractVersion.Current.ToString(),
            "fallbackplan-agent/0.1",
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
            runtime.KitConfirmation.Status,
            runtime.KitConfirmation.ConfirmedAtUnixMilliseconds);
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
