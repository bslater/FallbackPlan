using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A retention apply, or a person's deletion, beside a restore of the same
/// set that the service has under way (FR-GC-003, FR-GC-013; ADR-0172
/// Amendment 3). A restore reads the blobs of the snapshot it restores as it
/// goes, and a snapshot that expires, or that a person deletes, while it runs
/// would have them swept from under it. The service holds each restore it
/// runs under way from before it writes until it ends, and the pass holds
/// its blobs on that word, as it does beside a backup.
/// </summary>
[TestClass]
public sealed class RetentionBesideARestoreTests : IDisposable
{
    private const string Run = "0123456789abcdef";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task RetentionApply_WhileARestoreOfTheSetIsUnderWay_HoldsItsBlobsAndSaysSo()
    {
        var (runtime, handler, grant, set) = await StartWithABackupAsync();
        await using var _ = runtime;

        runtime.RestoreRuns.Begin(UnderWay(set.Name));
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var beside);
        Assert.Contains(
            line => line.Contains("held beside a restore:", StringComparison.Ordinal),
            beside.Lines,
            "an apply beside the set's restore held nothing: " + string.Join(" | ", beside.Lines));

        // With the run ended, nothing is held.
        runtime.RestoreRuns.Leave(Run);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var after);
        Assert.IsFalse(
            after.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", after.Lines));
    }

    [TestMethod]
    public async Task RetentionApply_WhileARestoreOfAnotherSetIsUnderWay_HoldsNothing()
    {
        var (runtime, handler, grant, _) = await StartWithABackupAsync();
        await using var __ = runtime;

        runtime.RestoreRuns.Begin(UnderWay("another set"));
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var report);
        Assert.IsFalse(
            report.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task DeleteSnapshots_WhileARestoreOfTheSetIsUnderWay_HoldsItsBlobsAndSaysSo()
    {
        // A person's deletion runs two passes back to back, so it would
        // reach the restored snapshot's blobs sooner than any scheduled pass.
        var (runtime, handler, grant, set) = await StartWithABackupAsync();
        await using var _ = runtime;

        // A set keeps one snapshot, so the one a person deletes, and a
        // restore reads, is the older of two.
        _harness.WriteSourceFile("docs/a.txt", "changed before the second backup");
        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var restored = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).First().SnapshotId;

        runtime.RestoreRuns.Begin(UnderWay(set.Name));
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await handler.ExecuteAsync(
                new DeleteSnapshotsCommand(set.Id, [restored], Apply: true, ReclaimGrant: grant), Timeout),
            out var deleted);
        Assert.Contains(
            line => line.Contains("held beside a restore:", StringComparison.Ordinal),
            deleted.Lines,
            "a deletion beside the set's restore held nothing: " + string.Join(" | ", deleted.Lines));
    }

    /// <summary>A run of the set the service holds under way, as it holds a restore through it.</summary>
    private static RestoreRunRecord UnderWay(string setName) => new()
    {
        Run = Run,
        SetName = setName,
        SnapshotId = new string('e', 32),
        OutputDirectory = "/srv/restored",
        BeganAt = 1_000,
    };

    private async Task<(ServiceRuntime Runtime, ServiceCommandHandler Handler, string Grant, BackupSetConfiguration Set)>
        StartWithABackupAsync()
    {
        _harness.WriteSourceFile("docs/a.txt", "the snapshot a restore reads");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await _harness.CreateRepositoryAsync();
        await _harness.SetupAsync();

        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        return (runtime, handler, grant, set);
    }
}
