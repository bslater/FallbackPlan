using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// How many of a set's newest backup's files each destination holds
/// (FR-SVC-006, FR-DEST-004, ADR-0088 Amendment 1): what a console's circle
/// shows. At rest it is worked out from what the ledger says each
/// destination was last delivered and what the catalogue says each file
/// needs, so no destination is listed to answer a status poll. While a sync
/// runs it is counted as the objects land. While a backup runs, the row says
/// whether the run writes to that destination, because the run's own
/// progress then moves its count.
/// </summary>
/// <remarks>
/// A destination holds a file when it holds all of the file's content. It
/// holds the backup whole only once it also holds the snapshot record that
/// names the files: the content can all be there while the record, which a
/// copy sends last, is not.
/// </remarks>
[TestClass]
public sealed class DestinationFilesHeldTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Status_AfterADirectShipBackup_ItsDestinationHoldsTheNewestBackupWhole()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime, DateTimeOffset.Now);

        var vault = await RowAsync(runtime, "vault");
        Assert.AreEqual(3L, vault.FilesTotal);
        Assert.AreEqual(3L, vault.FilesHeld);
        Assert.IsTrue(vault.HoldsNewest, "the run that published the backup delivered all of it here");
        Assert.IsFalse(vault.InRun, "no backup is running");
        Assert.IsFalse(vault.Syncing, "no sync is running");
    }

    [TestMethod]
    public async Task Status_ADestinationThatMissedTheNewestBackup_HoldsOnlyTheFilesWhoseContentItAlreadyHad()
    {
        // Both destinations take the first backup. The second runs while
        // the spare is unplugged: it changes one file and adds another, and
        // the spare holds neither — but it does hold the two files the second
        // backup left as they were, because their content is what the first
        // backup delivered there.
        Directory.CreateDirectory(Vault);
        Directory.CreateDirectory(Spare);
        WriteConfiguration(directShip: true, withSpare: true);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        var first = DateTimeOffset.Now;
        await BackUpAsync(runtime, first);
        Assert.IsTrue((await RowAsync(runtime, "spare")).HoldsNewest);

        Directory.Move(Spare, Spare + "-unplugged");
        _harness.WriteSourceFile("docs/b.txt", "bravo, rewritten after the first backup");
        _harness.WriteSourceFile("docs/d.txt", "delta, new since the first backup");
        await BackUpAsync(runtime, first.AddHours(2));

        var vault = await RowAsync(runtime, "vault");
        Assert.AreEqual(4L, vault.FilesTotal);
        Assert.AreEqual(4L, vault.FilesHeld);
        Assert.IsTrue(vault.HoldsNewest);

        var spare = await RowAsync(runtime, "spare");
        Assert.AreEqual(4L, spare.FilesTotal, "every destination is counted against the set's newest backup");
        Assert.AreEqual(2L, spare.FilesHeld, "the two files the second backup left alone, and only those");
        Assert.IsFalse(spare.HoldsNewest);
    }

    [TestMethod]
    public async Task Status_ADestinationNothingWasEverDeliveredTo_CountsNoFiles()
    {
        // Declared but never created on disk: nothing has ever reached it,
        // so there is nothing to count. Not counted is not "holds none" — a
        // client draws the one as unknown and the other as empty.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true, withSpare: true);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime, DateTimeOffset.Now);

        var spare = await RowAsync(runtime, "spare");
        Assert.IsNull(spare.FilesHeld);
        Assert.IsFalse(spare.HoldsNewest);
        Assert.AreEqual(3L, (await RowAsync(runtime, "vault")).FilesHeld);
    }

    [TestMethod]
    public async Task Status_WhileABackupRuns_NamesTheDestinationsItWritesTo()
    {
        // The run is held at its first file, after it has chosen where it
        // ships: the destination it writes to says so, and the one it left
        // out (unplugged) does not.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true, withSpare: true);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();
        var gate = new PauseGate();
        gate.Pause("held so the test can read the status mid-run");
        var now = DateTimeOffset.Now;
        var job = runtime.Jobs.Begin(set.Id, (ulong)now.ToUnixTimeMilliseconds());
        var run = BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, pauseGate: gate,
            cancellationToken: Timeout).AsTask();

        // A run that ends before it parks has said why: fail on that, not on a timeout.
        if (await Task.WhenAny(gate.Parked, run).WaitAsync(Timeout) == run)
        {
            Assert.Fail($"the run ended before it could be held: {(await run).Outcome} {(await run).Detail}");
        }

        Assert.IsTrue((await RowAsync(runtime, "vault")).InRun, "the held run writes to the vault");
        Assert.IsFalse((await RowAsync(runtime, "spare")).InRun, "the run left the unplugged spare out");

        gate.Resume();
        await run.WaitAsync(Timeout);
        Assert.IsFalse((await RowAsync(runtime, "vault")).InRun, "the run is over");
    }

    [TestMethod]
    public async Task Status_WhileASyncRuns_SaysSo_AndCountsWhatHasLanded()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime, DateTimeOffset.Now);

        using (var live = runtime.Holdings.Track(_harness.DocsSetId, "vault", total: 3))
        {
            live.Report(held: 1);
            var syncing = await RowAsync(runtime, "vault");
            Assert.IsTrue(syncing.Syncing);
            Assert.AreEqual(1L, syncing.FilesHeld, "a live sync's own count, not the ledger's");
            Assert.AreEqual(3L, syncing.FilesTotal);
            Assert.IsFalse(syncing.HoldsNewest, "nothing is whole while a sync is still moving it");
        }

        var settled = await RowAsync(runtime, "vault");
        Assert.IsFalse(settled.Syncing);
        Assert.AreEqual(3L, settled.FilesHeld);
        Assert.IsTrue(settled.HoldsNewest);
    }

    [TestMethod]
    public async Task Sync_ToADestinationThatHoldsNothingYet_CountsItsFilesUpAsTheContentLands()
    {
        // A staging set: the backup lands in staging, and the pass's sync
        // carries it to the vault. The sync counts from what the vault holds
        // when it starts — nothing — up to every file.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        var reports = new List<(long Held, long Total)>();
        runtime.Holdings.Reported = (setId, destination, held, total) =>
        {
            if (setId == _harness.DocsSetId && destination == "vault")
            {
                lock (reports)
                {
                    reports.Add((held, total));
                }
            }
        };

        await BackUpAsync(runtime, DateTimeOffset.Now);

        List<(long Held, long Total)> seen;
        lock (reports)
        {
            seen = [.. reports];
        }

        Assert.IsNotEmpty(seen, "the sync reported nothing as it went");
        Assert.AreEqual(0L, seen[0].Held, "the vault held nothing when the sync began");
        Assert.IsTrue(seen.All(report => report.Total == 3), "counted against the newest backup's three files");
        Assert.IsTrue(
            seen.Zip(seen.Skip(1)).All(pair => pair.Second.Held >= pair.First.Held),
            "a sync only ever adds to what a destination holds");
        Assert.AreEqual(3L, seen[^1].Held);

        var vault = await RowAsync(runtime, "vault");
        Assert.IsTrue(vault.HoldsNewest);
        Assert.AreEqual(3L, vault.FilesHeld);
    }

    [TestMethod]
    public async Task ASync_WhoseCatalogueCannotSayWhatTheFilesNeed_GoesOnUncounted()
    {
        // The live count is a display. A catalogue row it cannot read costs
        // the count, never the sync it rides: starting it answers "nothing
        // to count", and nothing is published as live.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false);
        WriteThreeFiles();

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime, DateTimeOffset.Now);

        var catalogue = Assert.ContainsSingle(Directory.GetFiles(_harness.StateDirectory, "catalogue-*.db"));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = catalogue, Pooling = false }.ToString()))
        {
            connection.Open();
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE blobs SET store_blob_key = x'01'
                WHERE blob_id IN (SELECT l.blob_id FROM object_locations l
                                  JOIN version_contents c ON c.object_id = l.object_id LIMIT 1);
                """;
            Assert.AreEqual(1, corrupt.ExecuteNonQuery(), "the corruption must land, or the test proves nothing");
        }

        var set = runtime.Configuration.BackupSets.Single();
        var archive = await runtime.ExistingArchiveAsync(set.Id, Timeout);
        Assert.IsNotNull(archive);

        Assert.IsNull(SyncCount.Start(runtime, set, "vault", archive));
        Assert.IsNull(runtime.Holdings.Find(set.Id, "vault"), "nothing is counted live that could not be counted");
    }

    private void WriteThreeFiles()
    {
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", "bravo");
        _harness.WriteSourceFile("docs/c.txt", "charlie");
    }

    private async Task BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private async Task<DestinationStatusDescriptor> RowAsync(ServiceRuntime runtime, string name)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(
            await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        return Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations.Where(row => row.Name == name));
    }

    private void WriteConfiguration(bool directShip, bool withSpare = false)
    {
        List<DestinationConfiguration> destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            },
        ];

        List<SetDestinationReference> references = [new SetDestinationReference { Ref = "vault" }];

        if (withSpare)
        {
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('e', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = Spare,
            });
            references.Add(new SetDestinationReference { Ref = "spare" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
            },
            Timeout);
    }
}
