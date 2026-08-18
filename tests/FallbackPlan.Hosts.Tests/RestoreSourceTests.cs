using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Restore sources over the service (ADR-0041, FR-RST-001/003/004/006): a
/// source handle opens the set's staging archive or a destination's replica,
/// answers its snapshots, and feeds the source-aware listing, plan and run;
/// the guided options — several paths, the original-location target, the
/// rename-beside and overwrite policies — behave as stated; and the receipt
/// is persisted where the result says it is.
/// </summary>
[TestClass]
public sealed class RestoreSourceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task RestoreSource_StagingThenReplicaAfterStagingLoss_ServesTheSameBytes()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        _harness.WriteSourceFile("photos/beach.jpg", new string('p', 4_000));
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        string snapshotId;
        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            await RunBackupAndWaitAsync(runtime, handler);

            // The staging source: snapshots answered on open, the listing and
            // a multi-path run served through the handle.
            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
                await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var staging);
            Assert.AreEqual("staging", staging.Location);
            var snapshot = Assert.ContainsSingle(staging.Snapshots);
            snapshotId = snapshot.SnapshotId;
            Assert.AreEqual(2, snapshot.Files);

            Assert.IsInstanceOfType<DirectoryResult>(
                await handler.ExecuteAsync(
                    new ListDirectoryCommand(snapshotId, null, Source: staging.SourceId), _timeout.Token),
                out var top);
            CollectionAssert.AreEquivalent(
                new[] { "notes.txt", "photos" },
                top.Entries.Select(entry => entry.Name).ToArray());

            Assert.IsInstanceOfType<RestorePlanResult>(
                await handler.ExecuteAsync(
                    new PlanRestoreCommand(
                        snapshotId, null, Source: staging.SourceId, Paths: ["notes.txt", "photos"]),
                    _timeout.Token),
                out var plan);
            Assert.AreEqual(2, plan.Files);
            Assert.AreEqual(0, plan.Conflicts);
            Assert.IsEmpty(plan.MissingObjects);

            var stagingOut = Path.Combine(_harness.WorkPath, "from-staging");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(
                    new RunRestoreCommand(
                        snapshotId, null, stagingOut,
                        Source: staging.SourceId, Paths: ["notes.txt"], InPlace: true),
                    _timeout.Token),
                out var fromStaging);
            Assert.AreEqual("complete", fromStaging.Outcome);
            Assert.AreEqual(
                "the notes to come back", File.ReadAllText(Path.Combine(stagingOut, "notes.txt")));
            Assert.IsNotNull(fromStaging.ReceiptPath);
            Assert.IsTrue(File.Exists(fromStaging.ReceiptPath), "the receipt is persisted (FR-RST-004)");

            Assert.IsInstanceOfType<AcknowledgedResult>(
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(staging.SourceId), _timeout.Token));
            Assert.IsInstanceOfType<AcknowledgedResult>(
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(staging.SourceId), _timeout.Token),
                "closing the closed acknowledges");
        }

        // The scenario replica restore exists for: the staging archive is
        // GONE, and the destination's replica — found by probing, since
        // nothing maps set to repository any more — brings the data back.
        Directory.Delete(_harness.ArchivesRoot, recursive: true);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var opened = await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "vault"), _timeout.Token);
            if (opened is ServiceError openError)
            {
                Assert.Fail($"replica open refused: {openError.Reason}: {openError.Message}");
            }

            Assert.IsInstanceOfType<RestoreSourceOpenedResult>(opened, out var replica);
            Assert.AreEqual("vault", replica.Location);
            Assert.AreEqual(snapshotId, Assert.ContainsSingle(replica.Snapshots).SnapshotId);

            var replicaOut = Path.Combine(_harness.WorkPath, "from-replica");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(
                    new RunRestoreCommand(
                        snapshotId, null, replicaOut, Source: replica.SourceId, InPlace: true),
                    _timeout.Token),
                out var fromReplica);
            Assert.AreEqual("complete", fromReplica.Outcome);
            Assert.AreEqual(
                "the notes to come back", File.ReadAllText(Path.Combine(replicaOut, "notes.txt")));
            Assert.AreEqual(
                new string('p', 4_000), File.ReadAllText(Path.Combine(replicaOut, "photos", "beach.jpg")));
        }
    }

    [TestMethod]
    public async Task RunRestore_ToTheOriginalLocation_RenameKeepsBothAndOverwriteReplaces()
    {
        await _harness.CreateRepositoryAsync();
        var original = _harness.WriteSourceFile("notes.txt", "as captured");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var source);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;

        // The live file moved on; the rename policy keeps it and lands the
        // captured version beside it under the dated name (ADR-0041).
        File.WriteAllText(original, "edited since the backup");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, string.Empty,
                    Source: source.SourceId, Paths: ["notes.txt"],
                    Target: "original", Existing: "rename", InPlace: true),
                _timeout.Token),
            out var renamed);
        Assert.AreEqual("complete", renamed.Outcome);
        Assert.AreEqual(1L, renamed.WrittenBeside);
        Assert.AreEqual(0L, renamed.Displaced);
        Assert.AreEqual("edited since the backup", File.ReadAllText(original));
        var beside = Directory.GetFiles(_harness.SourceRoot, "notes (restored *").Single();
        Assert.AreEqual("as captured", File.ReadAllText(beside));

        // Overwrite is the destructive, explicit choice — the live edit is
        // knowingly forfeited and the captured bytes take the name back.
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, string.Empty,
                    Source: source.SourceId, Paths: ["notes.txt"],
                    Target: "original", Existing: "overwrite", InPlace: true),
                _timeout.Token),
            out var overwritten);
        Assert.AreEqual("complete", overwritten.Outcome);
        Assert.AreEqual("as captured", File.ReadAllText(original));
    }

    [TestMethod]
    public async Task RunRestore_OriginalLocationWhoseSetIsGone_IsRefusedBeforeAnythingMoves()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var source);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;

        Assert.IsInstanceOfType<ConfigurationChangeResult>(
            await handler.ExecuteAsync(new DeleteBackupSetCommand("docs"), _timeout.Token));

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, string.Empty,
                    Source: source.SourceId, Target: "original", InPlace: true),
                _timeout.Token),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("no longer configured", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RestoreSource_AnUnknownHandle_IsAStatedExpiry()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "kept");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(new string('a', 32), null, Source: "notasource"), _timeout.Token),
            out var expired);
        Assert.AreEqual(ServiceErrorReason.NotFound, expired.Reason);
        Assert.Contains("expired", expired.Message, StringComparison.Ordinal);

        // A set that has never backed up has no staging source to open.
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                new string('b', 32), "fresh", _harness.SourceRoot, null, [], [], ["vault"])),
            _timeout.Token));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("fresh"), _timeout.Token), out var never);
        Assert.Contains("never backed up", never.Message, StringComparison.Ordinal);
    }

    /// <summary>Commands a backup and waits for completion — the SetChangeTests idiom.</summary>
    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        await watching;

        // The fan-out to the vault destination rides the backup; the replica
        // tests need it to have landed.
        Assert.IsInstanceOfType<SyncResult>(
            await handler.ExecuteAsync(new SyncCommand("docs", null), _timeout.Token));
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
