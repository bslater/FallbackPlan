using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Change handling for a backup set (ADR-0038, FR-SVC-009): the preview verb
/// classifies the live source against the last backup; a material edit over
/// the contract answers with what changed and queues a rescan whose finding
/// stands as one durable notice until the next backup completes; and the
/// run_backup full flag — silently dropped by the service before this —
/// genuinely disables reuse.
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

        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand(null), _timeout.Token), out var preview);

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
        // (Creating it is not an edit, so the create bare-acknowledges.)
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "fresh", _harness.SourceRoot, null, [], ["photos"], ["vault"])),
            _timeout.Token));
        Assert.IsInstanceOfType<SetChangePreviewResult>(
            await handler.ExecuteAsync(new PreviewSetChangesCommand("fresh"), _timeout.Token), out var fresh);
        Assert.IsNull(fresh.BaselineSnapshotId);
        Assert.AreEqual(2, fresh.New.Count);
        Assert.AreEqual(0, fresh.Unchanged);
    }

    [TestMethod]
    public async Task UpsertBackupSet_AMaterialEdit_RaisesOneNoticeThatTheNextBackupResolves()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // The operator excludes the photos. The answer names the change, and
        // the rescan's finding lands as a durable notice.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos"], ["vault"])),
            _timeout.Token), out var answer);
        Assert.Contains(line => line.Contains("exclude rules changed", StringComparison.Ordinal), answer.Lines);
        Assert.Contains(line => line.Contains("rescan", StringComparison.Ordinal), answer.Lines);

        var key = $"set-changed:{_harness.DocsSetId}";
        await WaitForAsync(() => runtime.Notices.Unacknowledged.Any(notice => notice.Key == key));
        var notice = runtime.Notices.Unacknowledged.Single(current => current.Key == key);
        Assert.Contains("1 no longer included", notice.Message);
        Assert.Contains("docs", notice.Message);

        // A second edit refreshes the same notice rather than raising a pile.
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], ["photos", "*.tmp"], ["vault"])),
            _timeout.Token));
        await WaitForAsync(() => runtime.Notices.Unacknowledged.Count(current => current.Key == key) == 1
            && runtime.Notices.Unacknowledged.Single(current => current.Key == key).Message
                .Contains("no longer included", StringComparison.Ordinal));
        Assert.AreEqual(notice.Id, runtime.Notices.Unacknowledged.Single(current => current.Key == key).Id,
            "a refresh keeps the notice's identity");

        // The status a person reads carries it.
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), _timeout.Token), out var status);
        Assert.Contains(line => line.Contains("reconfigured", StringComparison.Ordinal), status.Notices);

        // The next completed backup captured under the new settings — the
        // notice's condition has cleared and it resolves itself.
        await RunBackupAndWaitAsync(runtime, handler);
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(current => current.Key == key),
            "a completed backup resolves the set-changed notice");
    }

    [TestMethod]
    public async Task UpsertBackupSet_ScheduleOrFirstBackupCases_NeitherQueuesARescan()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        // A schedule edit changes when, not what: a plain acknowledgement,
        // no notice.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "daily at 03:00", [], [], ["vault"])),
            _timeout.Token));
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(
            notice => notice.Key == $"set-changed:{_harness.DocsSetId}"));

        // A material edit to a set that has never backed up has nothing to
        // compare with — the answer says so instead of pretending to rescan.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('c', 32), "fresh", _harness.SourceRoot, null, [], [], ["vault"])),
            _timeout.Token));
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('c', 32), "fresh", _harness.SourceRoot, null, [], ["*.tmp"], ["vault"])),
            _timeout.Token), out var fresh);
        Assert.Contains(line => line.Contains("has not backed up yet", StringComparison.Ordinal), fresh.Lines);
        Assert.IsFalse(runtime.Notices.Unacknowledged.Any(
            notice => notice.Key == $"set-changed:{new string('c', 32)}"));
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
}
