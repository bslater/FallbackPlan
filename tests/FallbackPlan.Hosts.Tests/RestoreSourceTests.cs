using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Restore sources over the service (ADR-0041; FR-RST-001, FR-RST-003,
/// FR-RST-004, FR-RST-006): a
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

    [TestMethod]
    public async Task RunRestore_AFileSpanningSeveralProductionSegments_ComesBackByteIdentical()
    {
        // Every earlier host-level restore payload was smaller than one
        // production segment (1 MiB), so no integration test had ever
        // restored a SEGMENTED file — through the contract, from staging or
        // from a replica. This one spans four segments and is compared
        // byte for byte from both.
        var big = new byte[3 * 1024 * 1024 + 12_345];
        new Random(41).NextBytes(big);

        await _harness.CreateRepositoryAsync();
        File.WriteAllBytes(Path.Combine(_harness.SourceRoot, "big.bin"), big);
        _harness.WriteSourceFile("notes.txt", "the small companion");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var staging);
        var snapshotId = Assert.ContainsSingle(staging.Snapshots).SnapshotId;

        var stagingOut = Path.Combine(_harness.WorkPath, "big-from-staging");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, stagingOut, Source: staging.SourceId, Paths: ["big.bin"], InPlace: true),
                _timeout.Token),
            out var fromStaging);
        Assert.AreEqual("complete", fromStaging.Outcome);
        Assert.IsTrue(
            big.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(stagingOut, "big.bin"))),
            "the segmented file did not round-trip from the staging source");

        // The same bytes from the replica source — the targeted blob load
        // walking a multi-segment manifest against a second store.
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "vault"), _timeout.Token),
            out var replica);
        var replicaOut = Path.Combine(_harness.WorkPath, "big-from-replica");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, replicaOut, Source: replica.SourceId, Paths: ["big.bin"], InPlace: true),
                _timeout.Token),
            out var fromReplica);
        Assert.AreEqual("complete", fromReplica.Outcome);
        Assert.IsTrue(
            big.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(replicaOut, "big.bin"))),
            "the segmented file did not round-trip from the replica source");
    }

    [TestMethod]
    public async Task RunRestore_OriginalLocationOnAMultiRootSet_SlicesPerRootAndMergesTheReceipt()
    {
        // The label-sliced half of the original-location mapping (ADR-0041):
        // a two-root set restores each label back into its own configured
        // root — one run, one merged receipt with label-prefixed paths —
        // and a label the configuration no longer carries is refused whole,
        // by name, before anything moves.
        await _harness.CreateRepositoryAsync();
        var notes = _harness.WriteSourceFile("notes.txt", "as captured in source");
        var media = Path.Combine(_harness.WorkPath, "media");
        Directory.CreateDirectory(media);
        var clip = Path.Combine(media, "clip.mp4");
        File.WriteAllText(clip, "as captured in media");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["vault"],
                Roots:
                [
                    new BackupRootDescriptor(_harness.SourceRoot),
                    new BackupRootDescriptor(media),
                ])),
            _timeout.Token));
        await RunBackupAndWaitAsync(runtime, handler);

        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs"), _timeout.Token), out var source);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;

        // Both live files have moved on since the capture.
        File.WriteAllText(notes, "edited in source");
        File.WriteAllText(clip, "edited in media");

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, string.Empty,
                    Source: source.SourceId, Paths: ["source/notes.txt", "media/clip.mp4"],
                    Target: "original", Existing: "rename", InPlace: true),
                _timeout.Token),
            out var result);
        Assert.AreEqual("complete", result.Outcome);
        Assert.AreEqual(2L, result.WrittenBeside);

        // Each label landed in ITS root; both live edits kept; the captured
        // versions sit beside them under the dated names.
        Assert.AreEqual("edited in source", File.ReadAllText(notes));
        Assert.AreEqual("edited in media", File.ReadAllText(clip));
        Assert.AreEqual(
            "as captured in source",
            File.ReadAllText(Directory.GetFiles(_harness.SourceRoot, "notes (restored *").Single()));
        Assert.AreEqual(
            "as captured in media",
            File.ReadAllText(Directory.GetFiles(media, "clip (restored *").Single()));

        // One merged receipt for the whole run, its paths re-prefixed with
        // the labels the operator selected by.
        Assert.IsNotNull(result.ReceiptPath);
        var receipt = File.ReadAllText(result.ReceiptPath);
        Assert.Contains("source/notes.txt", receipt, StringComparison.Ordinal);
        Assert.Contains("media/clip.mp4", receipt, StringComparison.Ordinal);

        // The configuration moves on: still multi-root, but 'media' is no
        // longer one of the labels — the refusal names it and offers the
        // folder target, and nothing on disk changes.
        var archive = Path.Combine(_harness.WorkPath, "archive");
        Directory.CreateDirectory(archive);
        Assert.IsInstanceOfType<ConfigurationChangeResult>(await handler.ExecuteAsync(
            new UpsertBackupSetCommand(new BackupSetDescriptor(
                _harness.DocsSetId, "docs", _harness.SourceRoot, "every 1h", [], [], ["vault"],
                Roots:
                [
                    new BackupRootDescriptor(_harness.SourceRoot),
                    new BackupRootDescriptor(archive),
                ])),
            _timeout.Token));
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, string.Empty,
                    Source: source.SourceId, Paths: ["media/clip.mp4"],
                    Target: "original", InPlace: true),
                _timeout.Token),
            out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("'media'", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual("edited in media", File.ReadAllText(clip));
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
        // tests need it to have LANDED. The explicit sync may meet the
        // automatic one mid-flight ("already syncing"), so the wait is on
        // the ledger, which whichever pass finishes will stamp.
        Assert.IsInstanceOfType<SyncResult>(
            await handler.ExecuteAsync(new SyncCommand("docs", null), _timeout.Token));
        while (runtime.DestinationSync.Find(_harness.DocsSetId, "vault")
            is not { State: FallbackPlan.Application.DestinationSyncState.InSync })
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
