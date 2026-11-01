using System.Runtime.Versioning;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A retention apply, or a person's deletion, beside a restore of the same
/// set that the service has under way (FR-GC-003, FR-GC-013; ADR-0172
/// Amendment 3). A restore reads the blobs of the snapshot it restores as it
/// goes, and a snapshot that expires, or that a person deletes, while it runs
/// would have them swept from under it. The service holds each restore it
/// runs under way from before it writes until it ends, and the pass holds
/// its blobs on that word, as it does beside a backup. A restore in direct
/// mode runs outside the service, and holds a marker of its repository in the
/// service's state directory while it runs; the pass holds on that too
/// (ADR-0172 Amendment 6). A service on a state directory of its own also
/// looks in the installation's, where a restore that named none holds its
/// marker, and passes over one it may not read (ADR-0172 Amendment 7).
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

    [TestMethod]
    public async Task RetentionApply_WhileARestoreInDirectModeOfTheSetsRepositoryIsUnderWay_HoldsItsBlobsAndSaysSo()
    {
        var (runtime, handler, grant, set) = await StartWithABackupAsync();
        await using var _ = runtime;
        var repository = (await runtime.ExistingArchiveAsync(set.Id, Timeout))!.Repository.RepositoryId;

        using (RestoresUnderWay.Hold(_harness.StateDirectory, repository, Run))
        {
            Assert.IsInstanceOfType<RetentionResult>(
                await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
                out var beside);
            Assert.Contains(
                line => line.Contains("held beside a restore:", StringComparison.Ordinal),
                beside.Lines,
                "an apply beside a restore in direct mode held nothing: " + string.Join(" | ", beside.Lines));
        }

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var after);
        Assert.IsFalse(
            after.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", after.Lines));
    }

    [TestMethod]
    public async Task RetentionApply_WhileARestoreInDirectModeOfAnotherRepositoryIsUnderWay_HoldsNothing()
    {
        var (runtime, handler, grant, _) = await StartWithABackupAsync();
        await using var __ = runtime;

        using var other = RestoresUnderWay.Hold(
            _harness.StateDirectory, Domain.Identifiers.RepositoryId.FromBytes(new byte[16]), Run);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var report);
        Assert.IsFalse(
            report.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
    }

    [TestMethod]
    public async Task DeleteSnapshots_WhileARestoreInDirectModeOfTheSetsRepositoryIsUnderWay_HoldsItsBlobsAndSaysSo()
    {
        var (runtime, handler, grant, set) = await StartWithABackupAsync();
        await using var _ = runtime;
        _harness.WriteSourceFile("docs/a.txt", "changed before the second backup");
        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var restored = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).First().SnapshotId;
        var repository = (await runtime.ExistingArchiveAsync(set.Id, Timeout))!.Repository.RepositoryId;

        using var underWay = RestoresUnderWay.Hold(_harness.StateDirectory, repository, Run);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await handler.ExecuteAsync(
                new DeleteSnapshotsCommand(set.Id, [restored], Apply: true, ReclaimGrant: grant), Timeout),
            out var deleted);
        Assert.Contains(
            line => line.Contains("held beside a restore:", StringComparison.Ordinal),
            deleted.Lines,
            "a deletion beside a restore in direct mode held nothing: " + string.Join(" | ", deleted.Lines));
    }

    [TestMethod]
    public async Task RetentionApply_ByAServiceOnAStateDirectoryOfItsOwn_WhileARestoreInDirectModeIsUnderWayInTheInstallations_HoldsItsBlobsAndSaysSo()
    {
        var installation = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "installation-state")).FullName;
        var (runtime, handler, grant, set) = await StartWithABackupAsync(installation);
        await using var _ = runtime;
        var repository = (await runtime.ExistingArchiveAsync(set.Id, Timeout))!.Repository.RepositoryId;

        using (RestoresUnderWay.Hold(installation, repository, Run))
        {
            Assert.IsInstanceOfType<RetentionResult>(
                await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
                out var beside);
            Assert.Contains(
                line => line.Contains("held beside a restore:", StringComparison.Ordinal),
                beside.Lines,
                "an apply beside a restore held in the installation's state directory held nothing: " + string.Join(" | ", beside.Lines));
        }

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var after);
        Assert.IsFalse(
            after.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", after.Lines));
    }

    [TestMethod]
    public async Task RetentionApply_ByAServiceOnAStateDirectoryOfItsOwn_WhereTheInstallationHasNone_HoldsNothingAndMakesNone()
    {
        var installation = Path.Combine(_harness.WorkPath, "no-installation-state");
        var (runtime, handler, grant, _) = await StartWithABackupAsync(installation);
        await using var __ = runtime;

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var report);
        Assert.IsFalse(
            report.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
            string.Join(" | ", report.Lines));
        Assert.IsFalse(Directory.Exists(installation), "a service makes no installation by looking for one");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a folder's mode is how POSIX keeps it from a process")]
    [UnsupportedOSPlatform("windows")]
    public async Task RetentionApply_ByAServiceOnAStateDirectoryOfItsOwn_WhereTheInstallationsMayNotBeRead_GoesAhead()
    {
        var installation = Directory.CreateDirectory(Path.Combine(_harness.WorkPath, "unreadable-state")).FullName;
        var (runtime, handler, grant, set) = await StartWithABackupAsync(installation);
        await using var _ = runtime;
        var repository = (await runtime.ExistingArchiveAsync(set.Id, Timeout))!.Repository.RepositoryId;

        // A marker nobody holds, in a folder this process may not list
        // unless it runs as root, which lists it and clears the marker.
        var folder = Path.Combine(installation, "restores-under-way", repository.ToString());
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, Run + ".lock"), [], Timeout);
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            Assert.IsInstanceOfType<RetentionResult>(
                await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
                out var report);
            Assert.IsFalse(
                report.Lines.Any(line => line.Contains("held beside a restore:", StringComparison.Ordinal)),
                string.Join(" | ", report.Lines));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    /// <summary>
    /// Starts the service on the harness's state directory with one backup
    /// made, taking <paramref name="installation"/> for the installation's
    /// state directory, or this test's own state directory where none is
    /// given, so no test looks at the machine's.
    /// </summary>
    private async Task<(ServiceRuntime Runtime, ServiceCommandHandler Handler, string Grant, BackupSetConfiguration Set)>
        StartWithABackupAsync(string? installation = null)
    {
        _harness.WriteSourceFile("docs/a.txt", "the snapshot a restore reads");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await _harness.CreateRepositoryAsync();
        await _harness.SetupAsync();

        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                InstallationStateDirectoryOverride = installation ?? _harness.StateDirectory,
            },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        var set = runtime.Configuration.BackupSets.Single();
        Assert.AreEqual("ran", (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)).Outcome);
        return (runtime, handler, grant, set);
    }
}
