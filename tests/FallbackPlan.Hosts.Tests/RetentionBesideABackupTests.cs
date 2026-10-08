using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A retention apply, or a person's deletion, beside a backup of the same set
/// that the service holds (FR-GC-003, FR-GC-013; ADR-0009 Amendment 8). The
/// journal shows a backup's intent in flight only while the time it declared
/// for itself runs, and a run parked outside its window can outlive that. The
/// service knows the run is there, queued, running or parked, and the pass
/// holds its blobs on that word too.
/// </summary>
[TestClass]
public sealed class RetentionBesideABackupTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task RetentionApply_WhileTheSetsBackupIsParked_HoldsItsBlobsAndSaysSo()
    {
        // Enough files that the capture is still scanning when the apply
        // arrives: the pause gate is checked between scan events.
        for (var i = 0; i < 1500; i++)
        {
            _harness.WriteSourceFile($"many/file-{i:d5}.txt", $"contents of file {i}");
        }

        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await _harness.CreateRepositoryAsync();
        await _harness.SetupAsync();

        // One writer worker, so the apply runs only once the backup parks.
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                MaxConcurrentBackupsOverride = 1,
            },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        var set = runtime.Configuration.BackupSets.Single();

        // Stamped three days back, so its write intent has outlived the hour
        // it declared and the day's margin: the journal no longer shows it in
        // flight, and only the service knows the run is there.
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddDays(-3), userInitiated: false);
        while (!runtime.Jobs.Jobs.Any(job =>
            job.BackupSetId == set.Id && job.State is JobState.Scanning or JobState.Publishing))
        {
            Assert.IsFalse(backup.IsCompleted, "the backup finished before the apply could arrive beside it");
            await Task.Delay(10, Timeout);
        }

        // A person's apply outranks a scheduled backup, so the backup parks
        // and the apply runs in its place (ADR-0047).
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var beside);
        Assert.Contains(
            line => line.Contains("held beside a backup:", StringComparison.Ordinal),
            beside.Lines,
            "an apply beside the set's parked backup held nothing: " + string.Join(" | ", beside.Lines));

        var outcome = await backup.WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

        // With the run settled, nothing is held.
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var after);
        Assert.IsFalse(
            after.Lines.Any(line => line.Contains("held beside a backup:", StringComparison.Ordinal)),
            string.Join(" | ", after.Lines));
    }

    [TestMethod]
    public async Task DeleteSnapshots_WhileTheSetsBackupIsParked_HoldsItsBlobsAndSaysSo()
    {
        // A person's deletion runs two passes back to back, with an audit
        // record between them to run out the grace, so it would reach a
        // backup's reused blob sooner than any scheduled pass.
        for (var i = 0; i < 1500; i++)
        {
            _harness.WriteSourceFile($"many/file-{i:d5}.txt", $"contents of file {i}");
        }

        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await _harness.CreateRepositoryAsync();
        await _harness.SetupAsync();

        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                MaxConcurrentBackupsOverride = 1,
            },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        var set = runtime.Configuration.BackupSets.Single();

        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        _harness.WriteSourceFile("many/file-00000.txt", "changed before the second backup");
        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var oldest = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).First().SnapshotId;

        _harness.WriteSourceFile("many/file-00001.txt", "changed before the third backup");
        var backup = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddDays(-3), userInitiated: false);
        while (!runtime.Jobs.Jobs.Any(job =>
            job.BackupSetId == set.Id && job.State is JobState.Scanning or JobState.Publishing))
        {
            Assert.IsFalse(backup.IsCompleted, "the backup finished before the deletion could arrive beside it");
            await Task.Delay(10, Timeout);
        }

        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await handler.ExecuteAsync(new DeleteSnapshotsCommand(set.Id, [oldest], Apply: true, ReclaimGrant: grant), Timeout),
            out var deleted);
        Assert.Contains(
            line => line.Contains("held beside a backup:", StringComparison.Ordinal),
            deleted.Lines,
            "a deletion beside the set's parked backup held nothing: " + string.Join(" | ", deleted.Lines));

        var outcome = await backup.WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }
}
