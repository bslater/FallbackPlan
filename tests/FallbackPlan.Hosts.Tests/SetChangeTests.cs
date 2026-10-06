using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;
using System.Threading.Channels;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Change handling for a backup set (ADR-0038, FR-SVC-009): the preview verb
/// classifies the live source against the last backup; a material edit over
/// the contract answers with what changed, starts a backup under the new
/// settings at once — after any run still capturing under the earlier ones —
/// and queues a rescan whose finding stands as one durable notice until a
/// backup under the new settings completes; and the run_backup full flag —
/// silently dropped by the service before this — genuinely disables reuse.
/// </summary>
[TestClass]
public sealed class SetChangeTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task PreviewSetChanges_AfterAWeekOfActivity_ReportsEachBucketExactly()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the original notes");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteSourceFile("photos/sunset.jpg", new string('s', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // The activity the next backup would see: one edit, one addition, one
        // deletion.
        _harness.WriteSourceFile("notes.txt", "the notes, edited since the backup");
        _harness.WriteSourceFile("added.txt", "a file the backup never saw");
        File.Delete(Path.Combine(_harness.SourceRoot, "photos", "sunset.jpg"));

        // Named in full: the deleted file is a name only the backup holds, so
        // the comparison is asked through a source unlocked with the
        // passphrase (FR-WOR-007).
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand(null, Source: source), _timeout.Token), out var preview);

        Assert.AreEqual("docs", preview.SetName);
        Assert.IsNotNull(preview.BaselineSnapshotId, "there is a last backup to compare with");
        Assert.AreEqual(1, preview.Unchanged);
        Assert.AreEqual("added.txt", Assert.ContainsSingle(preview.New.Sample));
        Assert.AreEqual("notes.txt", Assert.ContainsSingle(preview.Updated.Sample));
        Assert.AreEqual("photos/sunset.jpg", Assert.ContainsSingle(preview.Deleted.Sample));
        Assert.AreEqual(0, preview.NoLongerIncluded.Count);
        Assert.AreEqual(0, preview.Failures);

        // The same question under a draft rule, nothing saved: the excluded
        // photo is not "deleted" — it is on disk and the rules stopped
        // capturing it, which is a different finding.
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(
                new PreviewSetChangesCommand(null, ExcludeRules: ["photos"]), _timeout.Token),
            out var draft);
        Assert.AreEqual(2, draft.NoLongerIncluded.Count,
            "both photos leave the rules — including the one that also left the disk, because under "
            + "these rules its absence is not a loss the next backup would even see");
        Assert.AreEqual(0, draft.Deleted.Count);

        // A set that has never backed up still answers — everything is new.
        // Created by file edit, not the upsert verb: the verb queues the
        // set's first backup at once (ADR-0047), so "never backed up" would
        // be a race.
        _harness.AddConfiguredSet(new string('b', 32), "fresh", "vault", excludeRules: ["photos"]);
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("fresh"), _timeout.Token), out var fresh);
        Assert.IsNull(fresh.BaselineSnapshotId);
        Assert.AreEqual(2, fresh.New.Count);
        Assert.AreEqual(0, fresh.Unchanged);
    }

    [TestMethod]
    public async Task UpsertBackupSet_AMaterialEdit_BacksUpUnderTheNewSettingsAtOnce()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        var holds = new RunHolds();
        await using var runtime = await StartAsync(holds.EnterAsync);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // The operator excludes the photos. Saving that is asking for the
        // backup to stop holding them, so a backup under the new settings
        // starts now rather than at the next schedule.
        holds.Arm();
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos"], ["vault"])),
            _timeout.Token), out var answer);
        Assert.Contains(line => line.Contains("exclude rules changed", StringComparison.Ordinal), answer.Lines);

        var (held, release) = await holds.NextAsync(_timeout.Token);
        Assert.Contains(line => line.Contains($"queued as job {held}", StringComparison.Ordinal), answer.Lines);

        // Until it completes the last backup predates the settings, and the
        // rescan's finding stands as the set's notice.
        var key = $"set-changed:{_harness.DocsSetId}";
        await WaitForAsync(() => runtime.Notices.Unacknowledged.Any(notice => notice.Key == key
            && notice.Message.Contains("1 no longer included", StringComparison.Ordinal)));
        var notice = runtime.Notices.Unacknowledged.Single(current => current.Key == key);
        Assert.Contains("docs", notice.Message);

        // The status a person reads carries it.
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);
        Assert.Contains(line => line.Contains("reconfigured", StringComparison.Ordinal), status.Notices);

        release.SetResult();
        var run = await WaitForSettledAsync(runtime, held);
        Assert.AreEqual(JobState.Complete, run.State, run.Detail);
        Assert.AreEqual(1, run.Stats?.FilesSeen, "the run captured under the new rules, without the photos");
        await WaitForAsync(() => !runtime.Notices.Unacknowledged.Any(current => current.Key == key));
    }

    [TestMethod]
    public async Task UpsertBackupSet_AnEditDuringARunUnderTheEarlierSettings_IsFollowedByARunUnderTheNewOnes()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        var holds = new RunHolds();
        await using var runtime = await StartAsync(holds.EnterAsync);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        holds.Arm();
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        var (earlier, releaseEarlier) = await holds.NextAsync(_timeout.Token);

        // The edit lands while that run captures under the earlier rules. One
        // run of a set at a time, so the backup the edit asks for follows it.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos"], ["vault"])),
            _timeout.Token), out var answer);
        Assert.Contains(line => line.Contains($"follows job {earlier}", StringComparison.Ordinal), answer.Lines);

        var key = $"set-changed:{_harness.DocsSetId}";
        await WaitForAsync(() => runtime.Notices.Unacknowledged.Any(notice => notice.Key == key
            && notice.Message.Contains("1 no longer included", StringComparison.Ordinal)));

        releaseEarlier.SetResult();
        var (later, releaseLater) = await holds.NextAsync(_timeout.Token);
        Assert.AreNotEqual(earlier, later, "a second run follows the first");
        var first = runtime.Jobs.Jobs.Single(job => job.Id == earlier);
        Assert.AreEqual(2, first.Stats?.FilesSeen, "the earlier run captured under the earlier rules");
        Assert.IsTrue(runtime.Notices.Unacknowledged.Any(current => current.Key == key),
            "a run under the earlier settings does not resolve the notice about the new ones");

        releaseLater.SetResult();
        var run = await WaitForSettledAsync(runtime, later);
        Assert.AreEqual(JobState.Complete, run.State, run.Detail);
        Assert.AreEqual(1, run.Stats?.FilesSeen, "the following run captured under the new rules");
        await WaitForAsync(() => !runtime.Notices.Unacknowledged.Any(current => current.Key == key));
    }

    [TestMethod]
    public async Task UpsertBackupSet_EditsWhileARunIsUnderWay_AreFollowedByOneRunOnly()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        var holds = new RunHolds();
        await using var runtime = await StartAsync(holds.EnterAsync);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        holds.Arm();
        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        var (earlier, releaseEarlier) = await holds.NextAsync(_timeout.Token);

        // Two edits during the one run: the run that follows captures the
        // last of them, and there is one such run, not one per edit.
        foreach (var excluded in new[] { new[] { "photos" }, new[] { "photos", "*.tmp" } })
        {
            Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
                new UpsertBackupSetCommand(new BackupSetDescriptor(
                    _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], excluded, ["vault"])),
                _timeout.Token));
        }

        releaseEarlier.SetResult();
        var (later, releaseLater) = await holds.NextAsync(_timeout.Token);
        releaseLater.SetResult();
        var run = await WaitForSettledAsync(runtime, later);
        Assert.AreEqual(JobState.Complete, run.State, run.Detail);

        // Nothing else follows: the journal holds the baseline, the earlier
        // run and the one that followed it.
        holds.Disarm();
        await RunBackupAndWaitAsync(runtime, handler);
        Assert.AreEqual(4, runtime.Jobs.Jobs.Count(job => job.BackupSetId == _harness.DocsSetId),
            "the baseline, the run under way, the one run that followed it, and this last check");
    }

    [TestMethod]
    public async Task UpsertBackupSet_ARescanThatFinishesAfterTheBackup_LeavesNoNoticeBehind()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // The reader lane is busy, so the edit's rescan waits behind it while
        // the backup under the new settings runs to its end.
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
            "occupy-the-reader-lane", JobLane.Reader, UserInitiated: false, "hold the reader lane",
            async cancellationToken => await occupied.Task.WaitAsync(cancellationToken))));

        try
        {
            Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
                new UpsertBackupSetCommand(new BackupSetDescriptor(
                    _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos"], ["vault"])),
                _timeout.Token));
            var queued = Scheduler.LatestJobFor(runtime, _harness.DocsSetId)!;
            var run = await WaitForSettledAsync(runtime, queued);
            Assert.AreEqual(JobState.Complete, run.State, run.Detail);
            Assert.AreEqual(1, run.Stats?.FilesSeen, "the backup the edit queued captured under the new rules");
        }
        finally
        {
            // Whatever was asserted, the lane is let go: a job that never ends
            // would hold the runtime's disposal for ever.
            occupied.TrySetResult();
        }

        // Then the rescan runs; a marker queued behind it says when it has.
        var marked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
            "after-the-rescan", JobLane.Reader, UserInitiated: false, "mark the rescan's end",
            _ =>
            {
                marked.TrySetResult();
                return ValueTask.CompletedTask;
            })));
        await marked.Task.WaitAsync(_timeout.Token);

        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"set-changed:{_harness.DocsSetId}"),
            "a backup already captured these settings, so the rescan's finding is history, not a notice");
    }

    [TestMethod]
    public async Task UpsertBackupSet_AScheduleEdit_QueuesNothing_AndANeverBackedUpSetsEdit_QueuesItsFirstBackup()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);
        var runs = runtime.Jobs.Jobs.Count(job => job.BackupSetId == _harness.DocsSetId);

        // A schedule edit changes when, not what: a plain acknowledgement, no
        // notice, and no backup.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "daily at 03:00", [], [], ["vault"])),
            _timeout.Token));
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(
            notice => notice.Key == $"set-changed:{_harness.DocsSetId}"));
        Assert.AreEqual(runs, runtime.Jobs.Jobs.Count(job => job.BackupSetId == _harness.DocsSetId));

        // A material edit to a set that has never backed up has nothing to
        // compare with, so no rescan, but it is still asking for a backup
        // under the new settings: the set's first one starts now. The set is
        // created by file edit, not the upsert verb, whose own first backup
        // (ADR-0047) would race this.
        var fresh = new string('c', 32);
        _harness.AddConfiguredSet(fresh, "fresh", "vault");
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                fresh, "fresh", _harness.SourceRoot, null, [], ["*.tmp"], ["vault"])),
            _timeout.Token), out var answer);
        var first = Scheduler.LatestJobFor(runtime, fresh);
        Assert.IsNotNull(first, "the edit queued the set's first backup");
        Assert.Contains(
            line => line.Contains("first backup", StringComparison.Ordinal)
                && line.Contains($"queued as job {first}", StringComparison.Ordinal),
            answer.Lines);
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(notice => notice.Key == $"set-changed:{fresh}"));
        await WaitForSettledAsync(runtime, first);
    }

    [TestMethod]
    public async Task RunBackup_TheFullFlagOverTheService_GenuinelyDisablesReuse()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", new string('x', 100_000));
        _harness.WriteSourceFile("deep/more.txt", new string('y', 100_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await RunBackupAndWaitAsync(runtime, handler);

        // The second identical backup short-circuits every file (NFR-PERF-003)…
        var incremental = await RunBackupAndWaitAsync(runtime, handler);
        Assert.AreEqual(2, incremental.FilesReused, "nothing changed, so every file re-emits its prior version");

        // …and full re-captures every one. Before ADR-0038 the service
        // silently dropped the flag and this asserted 2.
        var full = await RunBackupAndWaitAsync(runtime, handler, full: true);
        Assert.AreEqual(0, full.FilesReused, "a full backup ignores prior versions");
    }

    /// <summary>Commands a backup, waits for completion, and answers the job's final progress record.</summary>
    private async Task<JobProgress> RunBackupAndWaitAsync(
        ServiceRuntime runtime, ServiceCommandHandler handler, bool full = false)
    {
        JobProgress? final = null;

        // Subscribed here, before the command — the ServiceTests idiom.
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        final = observation.Progress;
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", full), _timeout.Token));

        await watching;
        Assert.IsNotNull(final);
        return final;
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition())
        {
            _timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, _timeout.Token);
        }
    }

    private async Task<JobRecord> WaitForSettledAsync(ServiceRuntime runtime, string jobId)
    {
        JobRecord? row = null;
        await WaitForAsync(() =>
        {
            row = runtime.Jobs.Jobs.LastOrDefault(job => job.Id == jobId);
            return row?.State is JobState.Complete or JobState.CompletedWithFailures or JobState.Cancelled
                or JobState.FailedRecoverable or JobState.FailedPermanent;
        });
        return row!;
    }

    private async Task<ServiceRuntime> StartAsync(Func<string, CancellationToken, ValueTask>? enteredScanning = null)
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                EnteredScanning = enteredScanning,
            },
            _timeout.Token);
    }

    /// <summary>
    /// Holds each backup run as it enters Scanning, once armed, until the test
    /// lets that run go — so a run is caught mid-flight by construction rather
    /// than by a sighting on the progress stream, which can arrive after the
    /// run has finished.
    /// </summary>
    private sealed class RunHolds
    {
        private readonly Channel<(string JobId, TaskCompletionSource Release)> _held =
            Channel.CreateUnbounded<(string JobId, TaskCompletionSource Release)>();

        private int _armed;

        /// <summary>Holds every run that enters Scanning from now on.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Lets every run that enters Scanning from now on go straight through.</summary>
        public void Disarm() => Volatile.Write(ref _armed, 0);

        /// <summary>The <see cref="ServiceOptions.EnteredScanning"/> callback.</summary>
        public async ValueTask EnterAsync(string jobId, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _held.Writer.WriteAsync((jobId, release), cancellationToken);
            await release.Task.WaitAsync(cancellationToken);
        }

        /// <summary>The next run held, and the means of letting it go on.</summary>
        public ValueTask<(string JobId, TaskCompletionSource Release)> NextAsync(CancellationToken cancellationToken) =>
            _held.Reader.ReadAsync(cancellationToken);
    }
}
