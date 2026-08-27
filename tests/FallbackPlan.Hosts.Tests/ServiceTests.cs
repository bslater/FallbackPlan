using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The service (ADR-0028): sole writer role, a command surface, real job
/// states, and cancellation that lands in the journal.
/// Establishes FR-SVC-001.
/// </summary>
[TestClass]
public sealed class ServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task Service_WhileItRuns_PreventsASecondWriterTakingTheRole()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();

        // FR-SVC-002. This is the whole point of the wave: two processes on one
        // state directory are the same writer by construction, and the second
        // must be refused rather than silently drawing from the same sequence
        // space.
        var refused = Assert.ThrowsExactly<ClientStateException>(
            () => StateDirectoryLock.Acquire(_harness.StateDirectory, StateDirectoryLock.DirectRole));

        Assert.Contains(StateDirectoryLock.ServiceRole, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Service_CommandedByAClient_AnswersOverTheLocalBinding()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await using var listener = LocalServiceListener.Start(handler, _harness.StateDirectory);
        await using var client = await LocalServiceClient.ConnectAsync(
            _harness.StateDirectory, "test", _timeout.Token);

        Assert.IsInstanceOfType<BackupSetsResult>(await client.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var sets);
        Assert.ContainsSingle(sets.Sets);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(await client.ExecuteAsync(new DescribeServiceCommand(), _timeout.Token), out var description);
        Assert.AreEqual(ContractVersion.Current.ToString(), description.ContractVersion);
        Assert.IsFalse(description.RemoteBindingEnabled);
        Assert.AreEqual(_harness.StateDirectory, description.StateDirectory);
    }

    [TestMethod]
    public async Task Backup_CommandedByAClient_RunsAndReportsStatesBeyondScanning()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", new string('x', 200_000));
        _harness.WriteSourceFile("deep/more.txt", new string('y', 200_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var seen = new List<JobState>();

        // Subscribed here, on this thread, before the backup is commanded.
        // Calling WatchAsync inside the Task.Run below would leave the
        // subscription to whenever the pool got round to it, and a busy pool is
        // exactly when the first states would be missed.
        var progressEvents = runtime.Progress.WatchAsync(_timeout.Token);

        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in progressEvents)
                {
                    lock (seen)
                    {
                        seen.Add(progress.Progress.State);
                    }
                }
            },
            _timeout.Token);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<JobAcceptedResult>(await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token), out var accepted);
        Assert.IsNotEmpty(accepted.JobId);

        // Wait for the watcher, not for the job. The job reaching Complete says
        // the engine finished; it says nothing about whether the task draining
        // the progress channel has caught up, and reading `seen` before it has
        // is a second race distinct from the subscription one — subscribing
        // eagerly guarantees no event is missed, not that every event has been
        // observed yet. The channel is FIFO, so a watcher that has seen
        // Complete has seen everything before it.
        await WaitForAsync(() =>
        {
            lock (seen)
            {
                return seen.Contains(JobState.Complete);
            }
        });

        // FR-SVC-006 and ADR-0029 §5: before this, eight of fourteen states were
        // written nowhere, and a ten-hour backup announced `Scanning` and then
        // nothing at all.
        List<JobState> states;
        lock (seen)
        {
            states = [.. seen];
        }

        Assert.Contains(JobState.Scanning, states);
        Assert.Contains(JobState.Packing, states);
        Assert.Contains(JobState.Publishing, states);
        Assert.Contains(JobState.Complete, states);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        Assert.ContainsSingle(snapshots.Snapshots);

        // The backup chains a fan-out; once the ledger says the vault is in
        // sync, the snapshot listing carries the per-destination vocabulary
        // (FR-SNP-003) — verified, because a local-path sync reads its
        // sampled bytes back before recording success (peer-protocol 04).
        await WaitForAsync(() =>
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.State
                == FallbackPlan.Application.DestinationSyncState.InSync);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var replicated);
        var row = Assert.ContainsSingle(replicated.Snapshots);
        Assert.IsNotNull(row.Destinations);
        Assert.Contains("vault: verified", row.Destinations);

        await _timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watching);
    }

    [TestMethod]
    public async Task CancelJob_JobIsRunning_StopsItAndReportsTheCancelledState()
    {
        await _harness.CreateRepositoryAsync();

        // Enough barely-compressible content that the job is still mid-run
        // when the cancel lands after Scanning is observed on the stream.
        for (var i = 0; i < 24; i++)
        {
            _harness.WriteSourceFile($"bulk/file-{i:d2}.txt", RandomText(seed: i, length: 1_000_000));
        }

        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var seen = new List<JobState>();

        // Subscribed before the backup is commanded, for the same reason as
        // the progress test above: a late subscription misses early states.
        var progressEvents = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var progress in progressEvents)
                {
                    lock (seen)
                    {
                        seen.Add(progress.Progress.State);
                    }
                }
            },
            _timeout.Token);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<JobAcceptedResult>(await handler.ExecuteAsync(new RunBackupCommand(null, Full: false), _timeout.Token), out var accepted);

        await WaitForAsync(() =>
        {
            lock (seen)
            {
                return seen.Contains(JobState.Scanning);
            }
        });

        // The T-2 positive path (ADR-0029 §4): cancellation is a command with
        // an acknowledged outcome, not a signal whose effect nobody reports.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(new CancelJobCommand(accepted.JobId), _timeout.Token));

        await WaitForAsync(() =>
        {
            lock (seen)
            {
                return seen.Contains(JobState.Cancelled);
            }
        });

        // The cancelled state reached the progress stream, and the job never
        // pretended to complete.
        lock (seen)
        {
            Assert.DoesNotContain(JobState.Complete, seen);
        }

        // The job journal agrees: Cancelled, with the command named as the
        // reason — a job that stayed `Publishing` forever was the T-2 finding.
        Assert.IsInstanceOfType<JobsResult>(await handler.ExecuteAsync(new ListJobsCommand(ActiveOnly: false), _timeout.Token), out var jobs);
        var job = jobs.Jobs.Single(descriptor => descriptor.Id == accepted.JobId);
        Assert.AreEqual(JobState.Cancelled, job.State);
        Assert.AreEqual("cancelled by request", job.Detail);

        // A second cancel finds no active job to stop, and says so.
        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(new CancelJobCommand(accepted.JobId), _timeout.Token), out var error);
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);

        await _timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => watching);
    }

    [TestMethod]
    public async Task Cancel_WhenTheJobIsNotRunning_SaysSoRatherThanPretending()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(new CancelJobCommand("no-such-job"), _timeout.Token), out var error);

        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    [TestMethod]
    public async Task Verify_CommandedThroughTheContract_ChecksTheBlobsTheServiceHolds()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        // This used to answer "read path, run it directly". The refusal was
        // honest while it stood, but a service that cannot verify its own
        // repository is a service a console cannot ask the only question that
        // matters after a backup.
        Assert.IsInstanceOfType<VerificationResult>(await handler.ExecuteAsync(new VerifyCommand("records"), _timeout.Token), out var verified);

        Assert.IsTrue(verified.ObjectsChecked > 0, "the repository holds blobs, so the sweep must have examined some");
        Assert.AreEqual(0, verified.Failures);
        Assert.AreEqual("records", verified.Level);
    }

    [TestMethod]
    public async Task Verify_LevelIsUnknown_RefusesListingTheVocabulary()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(new VerifyCommand("paranoid"), _timeout.Token), out var error);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("locator | digest | records", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Check_AnUndamagedRepository_ReportsItClean()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<CheckResult>(await handler.ExecuteAsync(new CheckCommand("digest"), _timeout.Token), out var check);

        // Findings only, so empty means "nothing is wrong" rather than
        // "nothing ran" — the counts of what was healthy are not findings.
        Assert.IsEmpty(check.Findings);
    }

    [TestMethod]
    public async Task RestorePlan_BeforeAnythingMoves_ReportsWhatItWouldWrite()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        Assert.IsInstanceOfType<RestorePlanResult>(await handler.ExecuteAsync(new PlanRestoreCommand(snapshot.SnapshotId, null), _timeout.Token), out var plan);

        Assert.AreEqual(2, plan.Files);
        Assert.IsTrue(plan.Bytes > 0);
        Assert.IsEmpty(plan.MissingObjects);
    }

    [TestMethod]
    public async Task RestorePlan_TheCatalogueIsAheadOfTheStore_ReportsTheObjectsAsMissing()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        // The store loses the metadata blobs the plan's objects live in — a
        // provider rollback, another participant's compaction — and nothing
        // tells the catalogue, whose rows still locate every one of them.
        foreach (var blob in Directory.EnumerateFiles(
            Path.Combine(_harness.RepositoryPath, "blobs", "meta"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        Assert.IsInstanceOfType<RestorePlanResult>(await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshot.SnapshotId, null), _timeout.Token), out var plan);

        // The plan's whole purpose (its own comment says so): the objects it
        // needs and cannot find, before any byte moves. Asking the catalogue
        // whether the catalogue knows a location answers a different
        // question — a catalogue ahead of the store says "nothing missing"
        // and the failure reappears mid-restore, mislabelled. The plan must
        // ask the store.
        Assert.IsNotEmpty(plan.MissingObjects);
        Assert.Contains("notes.txt", plan.MissingObjects);
    }

    [TestMethod]
    public async Task RestorePlan_TheStoreLosesTheDataBlobs_ReportsTheFilesAsMissing()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        // Only the DATA blobs go — the metadata blobs holding the manifests
        // stay, which is exactly what a staging trim leaves behind
        // (ADR-0034 §6). A probe that stops at the manifest's blob calls this
        // snapshot whole; the plan must follow the manifest's segment
        // references into the store to be honest about it (FR-RST-003).
        foreach (var blob in Directory.EnumerateFiles(
            Path.Combine(_harness.RepositoryPath, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        Assert.IsInstanceOfType<RestorePlanResult>(await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshot.SnapshotId, null), _timeout.Token), out var plan);

        Assert.IsNotEmpty(plan.MissingObjects);
        Assert.Contains("notes.txt", plan.MissingObjects);
    }

    [TestMethod]
    public async Task Restore_CommandedThroughTheContract_WritesOnTheServiceMachine()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<RestoreResult>(await handler.ExecuteAsync(
                new RunRestoreCommand(snapshot.SnapshotId, null, destination), _timeout.Token), out var restored);

        // ADR-0028 §6: the output directory is a path on the machine running the
        // service. A caller is told what happened and never sent the files, so
        // the proof of a restore is on this disk rather than in the result.
        Assert.AreEqual(2, restored.Restored);
        Assert.AreEqual(0, restored.Failed);

        // The receipt outcome reaches the caller (FR-RST-005). Carrying it is
        // what stops a Partial restore — one that failed nothing but skipped a
        // required item — being read as success from Failed == 0 alone.
        Assert.AreEqual("complete", restored.Outcome);

        // Quarantine by default (FR-RST-006): the content lands under the
        // commanded directory rather than directly in it, and the result says
        // where — a caller told the commanded path could not find its files.
        Assert.StartsWith(destination, restored.OutputDirectory, StringComparison.Ordinal);
        Assert.AreNotEqual(destination, restored.OutputDirectory);

        Assert.AreEqual(
            "hello",
            await File.ReadAllTextAsync(Path.Combine(restored.OutputDirectory, "notes.txt"), _timeout.Token));
        Assert.AreEqual(
            "deeper",
            await File.ReadAllTextAsync(
                Path.Combine(restored.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task Restore_CommandedTwiceForOneSnapshot_UsesADistinctRunDirectoryEachTime()
    {
        // Architecture 08 §3.1: each run's displaced and quarantined content is
        // namespaced by run, so two runs cannot overwrite one another's. The
        // run identifier was once derived from the snapshot id, which made every
        // restore of a snapshot share one store — a single shared refuge, worse
        // than none.
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        var snapshot = Assert.ContainsSingle(snapshots.Snapshots);

        var destination = Path.Combine(_harness.WorkPath, "twice");

        Assert.IsInstanceOfType<RestoreResult>(await handler.ExecuteAsync(
                new RunRestoreCommand(snapshot.SnapshotId, null, destination), _timeout.Token), out var first);
        Assert.IsInstanceOfType<RestoreResult>(await handler.ExecuteAsync(
                new RunRestoreCommand(snapshot.SnapshotId, null, destination), _timeout.Token), out var second);

        Assert.AreNotEqual(first.OutputDirectory, second.OutputDirectory,
            "two restores of one snapshot must land in distinct run directories");
    }

    [TestMethod]
    public async Task Restore_SnapshotIsUnknown_ReportsNotFoundRatherThanEmpty()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(await handler.ExecuteAsync(
                new PlanRestoreCommand(new string('a', 32), null), _timeout.Token), out var error);

        // An empty plan and an absent snapshot are different answers, and a
        // caller that cannot tell them apart cannot report either honestly.
        Assert.AreEqual(ServiceErrorReason.NotFound, error.Reason);
    }

    [TestMethod]
    public async Task Status_AskedOfTheService_IsDerivedInOnePlace()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteConfiguration("every 1h");
        await _harness.BackUpAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);

        // The client receives the derivation, never the inputs to redo it
        // (10 §3.1) — a front end that computed its own would be a second
        // implementation of the never-merge rules.
        var set = Assert.ContainsSingle(status.Sets);
        Assert.IsNotNull(set.NextRun);
        Assert.AreEqual(Environment.MachineName, status.MachineName);

        // And beneath the roll-up, the matrix: one row per declared
        // destination, naming where the pair stands (FR-DEST-004) — present
        // even for a set with no snapshot, because "which destinations does
        // this set have and where do they stand" is a question with an
        // answer before the first backup. (The CLI backup above wrote under
        // its own default set, so "docs" itself has never backed up.)
        var row = Assert.ContainsSingle(set.Destinations);
        Assert.AreEqual("vault", row.Name);
        Assert.AreEqual("local-path", row.Kind);
        Assert.AreEqual("behind", row.State);
        Assert.AreEqual(FallbackPlan.Domain.Status.ProtectionState.NeverBackedUp, set.Status.State);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task UpsertBackupSet_CreatingAndEditing_IsTheOnlyWayAClientMakesASetAndItWorks()
    {
        // The only programmatic route by which a client — a UI, eventually —
        // creates or edits a backup set. It writes the configuration file the
        // scheduler then reads, and nothing exercised it.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var created = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "photos", _harness.SourceRoot, "every 6h", [], [], ["vault"])),
            _timeout.Token);

        // Creation answers with its queued first backup (ADR-0047): saving a
        // set IS asking for it to run.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(created, out var creation);
        Assert.IsTrue(creation.Lines.Any(line => line.Contains("First backup", StringComparison.Ordinal)));

        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var afterCreate);
        Assert.HasCount(2, afterCreate.Sets);
        var photos = afterCreate.Sets.Single(set => set.Name == "photos");
        Assert.AreEqual("every 6h", photos.Schedule);
        Assert.AreEqual("vault", photos.Destinations.Single());

        // An edit replaces the row rather than appending a second one with the
        // same identity, which is what "upsert" has to mean for a file the
        // scheduler iterates. Adding a rule is a material edit, so since
        // ADR-0038 the answer names what changed rather than bare-acknowledging.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "photos", _harness.SourceRoot, "daily at 02:30", ["**/*.jpg"], [], ["vault"])),
            _timeout.Token), out var materialEdit);
        Assert.Contains(line => line.Contains("include rules changed", StringComparison.Ordinal), materialEdit.Lines);

        Assert.IsInstanceOfType<BackupSetsResult>(
            await handler.ExecuteAsync(new ListBackupSetsCommand(), _timeout.Token), out var afterEdit);
        Assert.HasCount(2, afterEdit.Sets);
        var edited = afterEdit.Sets.Single(set => set.Id == new string('b', 32));
        Assert.AreEqual("daily at 02:30", edited.Schedule);
        Assert.AreEqual("**/*.jpg", edited.IncludeRules.Single());
    }

    [TestMethod]
    public async Task UpsertBackupSet_EditingASetWithRetentionOverrides_KeepsWhatTheCommandCannotCarry()
    {
        // The subtle half, and the reason this is worth a test of its own: the
        // command names destinations as bare strings, so a per-destination
        // retention override has no field to travel in. An upsert that took
        // the command at face value would silently widen what that destination
        // is entitled to keep — and nothing downstream would report it,
        // because a destination keeping MORE looks healthy.
        var overridden = new RetentionConfiguration { KeepDaily = 3, MinGenerations = 1 };
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32), Name = "vault",
                    Kind = DestinationKind.LocalPath, Path = Path.Combine(_harness.StateDirectory, "vault"),
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Retention = new RetentionConfiguration { KeepDaily = 7, MinGenerations = 2 },
                    Destinations = [new SetDestinationReference { Ref = "vault", Retention = overridden }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        await _harness.CreateRepositoryAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 4h", [], [], ["vault"])),
            _timeout.Token));

        var reloaded = ClientConfiguration.Load(Path.Combine(_harness.StateDirectory, "config.json"));
        var set = reloaded.FindSet("docs")!;
        Assert.AreEqual("every 4h", set.Schedule, "what the command did carry is applied");
        Assert.AreEqual(7, set.Retention?.KeepDaily, "the set's own policy survives an edit that could not name it");
        Assert.AreEqual(
            3, set.Destinations.Single().Retention?.KeepDaily,
            "and so does the per-destination override, matched back by name");
    }

    [TestMethod]
    public async Task UpsertBackupSet_NamingADestinationNobodyDeclared_IsRefusedWithoutTouchingTheFile()
    {
        // Validation runs on save, so a bad set is refused here rather than
        // discovered by the scheduler at two in the morning (FR-DEST-001). It
        // must come back as a ServiceError — an exception crossing the command
        // boundary is not an answer a client can act on — and the file on disk
        // must be exactly what it was.
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        var configurationPath = Path.Combine(_harness.StateDirectory, "config.json");
        var before = await File.ReadAllTextAsync(configurationPath, _timeout.Token);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var refused = await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('c', 32), "elsewhere", _harness.SourceRoot, "every 1h", [], [], ["nowhere"])),
            _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
        Assert.Contains("nowhere", error.Message, StringComparison.Ordinal);
        Assert.AreEqual(before, await File.ReadAllTextAsync(configurationPath, _timeout.Token));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            passphrase,
            _timeout.Token);
    }

    private static string RandomText(int seed, int length)
    {
        // Printable and barely compressible: the point is pipeline work per
        // byte, so the job is still running when the cancel arrives.
        var random = new Random(seed);
        var characters = new char[length];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = (char)('!' + random.Next(94));
        }

        return new string(characters);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }
}
