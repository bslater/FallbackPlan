using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A staging set's road to direct-ship (ADR-0046's migration slice): flagging
/// the set migrates its metadata into the local metadata store at first open,
/// the staging archive stays on disk as a read-only seed source until every
/// object it holds has reached a destination, and only the explicit
/// retire_staging verb — refused while anything would be lost — deletes
/// it (FR-DEST-016).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipMigrationTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string StagingPath => Path.Combine(_harness.ArchivesRoot, _harness.DocsSetId);

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AStagingSetFlaggedDirectShip_MigratesSeedsAndRetiresOnlyWhenNothingWouldBeLost()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/history.txt", new string('h', 60_000) + "the first era's bytes");

        // ---- The classic era: a staging capture fanned out by the pass (so
        // the destination holds a baseline), then a second capture the
        // fan-out never carried — history only staging holds, which is
        // exactly what retirement must refuse to lose.
        string historySnapshot;
        await using (var runtime = await StartAsync())
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);
            Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!.BaselineCompletedAt);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<SnapshotsResult>(
                await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
            historySnapshot = Assert.ContainsSingle(listed.Snapshots).SnapshotId;

            _harness.WriteSourceFile("docs/second-era.txt", new string('s', 40_000) + "unsynced history");
            Assert.AreEqual(
                "ran",
                (await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(),
                    DateTimeOffset.Now.AddMinutes(1), userInitiated: true).WaitAsync(Timeout)).Outcome);
        }

        Assert.IsTrue(File.Exists(Path.Combine(StagingPath, "repository-format")));

        // ---- The flip: same set, direct_ship = true. The next open migrates
        // metadata; the next capture ships new blobs to the destination while
        // history blobs stay only in staging — reuse still works, because the
        // sink falls back to reading staging until it is retired.
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/fresh.txt", "the direct-ship era's bytes");

        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var set = runtime.Configuration.BackupSets.Single();

            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

            Assert.IsTrue(
                File.Exists(Path.Combine(MetadataRoot, "repository-format")),
                "the flip must migrate the metadata store into place");
            Assert.IsTrue(File.Exists(Path.Combine(StagingPath, "repository-format")),
                "migration must never delete staging on its own");
            Assert.IsTrue(
                runtime.Notices.Unacknowledged.Any(notice =>
                    notice.Key == $"staging-retirable:{_harness.DocsSetId}"),
                "a standing notice must say staging awaits retirement");

            // Retirement is refused while staging holds history no
            // destination has — the destination received only the fresh
            // capture's blobs so far.
            Assert.IsInstanceOfType<ServiceError>(
                await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var early);
            Assert.AreEqual(ServiceErrorReason.Refused, early.Reason);
            Assert.Contains("not reached", early.Message, StringComparison.Ordinal);

            // The pass seeds the destination from staging through the sink.
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(2), Timeout);
            await pass.Transfers.WaitAsync(Timeout);

            // Now nothing would be lost, and retirement proceeds.
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var retired);
            Assert.IsTrue(retired.Lines.Any(line => line.Contains("retired", StringComparison.Ordinal)));
            Assert.IsFalse(Directory.Exists(StagingPath), "retirement deletes the staging archive");
            Assert.IsFalse(
                runtime.Notices.Unacknowledged.Any(notice =>
                    notice.Key == $"staging-retirable:{_harness.DocsSetId}"),
                "retirement resolves the standing notice");

            // The history restores from the destination alone.
            var output = Path.Combine(_harness.WorkPath, "restored");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(new RunRestoreCommand(historySnapshot, null, output), Timeout),
                out var restored);
            Assert.AreEqual("complete", restored.Outcome);
            var recovered = Assert.ContainsSingle(
                Directory.GetFiles(output, "history.txt", SearchOption.AllDirectories));
            Assert.Contains(
                "the first era's bytes", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task TheMigrationSurvivesARestart_AndSoDoesRetirement()
    {
        // The migration is a first-open side effect, and services restart:
        // a second open of an already-migrated set must change nothing
        // (idempotence), retirement must work from a process that did not
        // perform the migration, and a post-retirement restart must answer
        // restores with the staging fallback gone for good.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/first.txt", "the staging era's bytes");

        string firstSnapshot;
        await using (var runtime = await StartAsync())
        {
            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            Assert.AreEqual(1, pass.Ran);
            await pass.Transfers.WaitAsync(Timeout);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<SnapshotsResult>(
                await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
            firstSnapshot = Assert.ContainsSingle(listed.Snapshots).SnapshotId;
        }

        WriteConfiguration(directShip: true);

        // Open one: the flip migrates.
        await using (var runtime = await StartAsync())
        {
            var set = runtime.Configuration.BackupSets.Single();
            Assert.AreEqual(
                "ran",
                (await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                    .WaitAsync(Timeout)).Outcome);
            Assert.IsTrue(File.Exists(Path.Combine(MetadataRoot, "repository-format")));
        }

        // Open two, migration already done: captures keep working and the
        // pass finishes seeding — a restart between the flip and the
        // retirement is the normal course of events, not a special case.
        _harness.WriteSourceFile("docs/second.txt", "the restarted era's bytes");
        await using (var runtime = await StartAsync())
        {
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(2), userInitiated: true)
                .WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);

            var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now.AddMinutes(4), Timeout);
            await pass.Transfers.WaitAsync(Timeout);

            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            Assert.IsInstanceOfType<ConfigurationChangeResult>(
                await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var retired);
            Assert.IsTrue(retired.Lines.Any(line => line.Contains("retired", StringComparison.Ordinal)));
            Assert.IsFalse(Directory.Exists(StagingPath));
        }

        // Open three, staging gone: the history restores from the
        // destination, and new captures keep publishing.
        await using (var runtime = await StartAsync())
        {
            var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
            var output = Path.Combine(_harness.WorkPath, "after-restart");
            Assert.IsInstanceOfType<RestoreResult>(
                await handler.ExecuteAsync(new RunRestoreCommand(firstSnapshot, null, output), Timeout),
                out var restored);
            Assert.AreEqual("complete", restored.Outcome);
            Assert.AreEqual(
                "the staging era's bytes",
                await File.ReadAllTextAsync(Assert.ContainsSingle(
                    Directory.GetFiles(output, "first.txt", SearchOption.AllDirectories)), Timeout));

            _harness.WriteSourceFile("docs/third.txt", "after retirement and restart");
            var set = runtime.Configuration.BackupSets.Single();
            var outcome = await Scheduler
                .Enqueue(runtime, set, DateTimeOffset.Now.AddMinutes(6), userInitiated: true).WaitAsync(Timeout);
            Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
        }
    }

    [TestMethod]
    public async Task RetireStaging_AStagingModeSet_IsRefusedByName()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        _harness.WriteSourceFile("docs/a.txt", "words");

        await using var runtime = await StartAsync();
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("direct-ship", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetireStaging_WithNoStagingArchive_IsRefusedByName()
    {
        // A direct-ship set that never lived on staging has nothing to
        // retire; the verb says so instead of failing into the union check.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("no staging archive", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RetireStaging_AnUnreachableDestination_IsRefusedBeforeAnythingIsDeleted()
    {
        // An absent drive might be the only holder of something staging is
        // about to stop holding: retirement demands every referenced
        // destination present, and the staging archive survives the refusal.
        WriteConfiguration(directShip: true);
        await _harness.CreateRepositoryAsync();

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("not reachable", refused.Message, StringComparison.Ordinal);
        Assert.IsTrue(Directory.Exists(StagingPath), "a refused retirement must not touch the archive");
    }

    [TestMethod]
    public async Task RetireStaging_ADestinationWithADefectiveAddress_IsRefusedWithTheDefect()
    {
        // A relative destination path is a stored defect (ADR-0037's repair
        // flow), and retirement must repeat it rather than resolve the path
        // against the service's working directory and "prove" the wrong tree.
        await _harness.CreateRepositoryAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_harness.StateDirectory, "config.json"),
            $$"""
            {
              "schema_version": {{ClientConfiguration.CurrentSchemaVersion}},
              "destinations": [
                { "id": "{{new string('d', 32)}}", "name": "vault", "kind": "local-path", "path": "relative/vault" }
              ],
              "backup_sets": [
                {
                  "id": "{{_harness.DocsSetId}}",
                  "name": "docs",
                  "roots": [ { "path": {{System.Text.Json.JsonSerializer.Serialize(_harness.SourceRoot)}} } ],
                  "schedule": "every 1h",
                  "direct_ship": true,
                  "destinations": [ "vault" ]
                }
              ]
            }
            """,
            Timeout);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RetireStagingCommand("docs"), Timeout), out var refused);
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains("relative", refused.Message, StringComparison.Ordinal);
        Assert.IsTrue(Directory.Exists(StagingPath), "a refused retirement must not touch the archive");
    }

    private void WriteConfiguration(bool directShip) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                Destinations = [new SetDestinationReference { Ref = "vault" }],
                DirectShip = directShip,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

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
            Timeout);
    }
}
