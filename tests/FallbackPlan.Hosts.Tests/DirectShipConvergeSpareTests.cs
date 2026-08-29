using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The converge spare against a direct-ship set (ADR-0046): with no staging
/// archive, the destinations are the only holders — so one destination's
/// policy trim must never delete the last copy of a snapshot a sibling is
/// still owed, and the spare must release once the sibling provably holds
/// it. Establishes the direct-ship shape of FR-GC-009 (a destination offline
/// beyond a sibling's retention window does not cause history to be lost
/// from every replica silently) and exercises FR-GC-010's per-destination
/// overrides against the sink.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipConvergeSpareTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultA => Path.Combine(_harness.WorkPath, "vault-a");
    private string VaultB => Path.Combine(_harness.WorkPath, "vault-b");
    private string VaultBUnplugged => Path.Combine(_harness.WorkPath, "vault-b-unplugged");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Converge_ASiblingStillOwedSnapshots_SparesTheirClosureUntilDelivered()
    {
        Directory.CreateDirectory(VaultA);
        Directory.CreateDirectory(VaultB);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "first content");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var setId = _harness.DocsSetId;

        // Both destinations take the first backup, and the sync pass stamps
        // both ledgers with the sequence it proves delivered — the record the
        // spare decision reads.
        await BackUpAsync(runtime);
        await SyncAllAsync(runtime, setId);

        // vault-b is unplugged and misses two backups that therefore land on
        // vault-a alone. All three snapshots share one daily bucket, so
        // vault-a's KeepDaily=1 + MinGenerations=1 keeps only the newest —
        // exactly the pressure that used to delete the middle one's last copy.
        Directory.Move(VaultB, VaultBUnplugged);
        _harness.WriteSourceFile("docs/a.txt", "second content");
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/a.txt", "third content");
        await BackUpAsync(runtime);

        // The operator's sync (or any heal) converges vault-a while vault-b
        // is still owed the second and third snapshots. The first, provably
        // delivered to vault-b, goes; the owed two are spared even though
        // vault-a's own policy keeps only the newest.
        await SyncAllAsync(runtime, setId);
        var replicaA = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        Assert.HasCount(2, SnapshotObjects(replicaA));

        // vault-b returns and the next sync delivers the whole owed history
        // out of the spared copies.
        Directory.Move(VaultBUnplugged, VaultB);
        await SyncAllAsync(runtime, setId);
        var replicaB = Assert.ContainsSingle(Directory.GetDirectories(VaultB));
        Assert.HasCount(3, SnapshotObjects(replicaB));

        // Delivered, the spare releases: the next sync returns vault-a to
        // exactly its keep-set (FR-GC-010's steady state) — the extra copies
        // were held only while they were the last ones.
        await SyncAllAsync(runtime, setId);
        Assert.ContainsSingle(SnapshotObjects(replicaA));
        Assert.HasCount(3, SnapshotObjects(replicaB));

        // The proof that matters: with vault-a gone, the once-owed middle
        // snapshot restores from vault-b alone — its closure crossed whole,
        // not just its record.
        Directory.Move(VaultA, VaultA + "-retired");
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var middle = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).ElementAt(1);
        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(new RunRestoreCommand(middle.SnapshotId, null, output), Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "a.txt", SearchOption.AllDirectories));
        Assert.Contains("second content", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
    }

    private int _runs;

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var when = DateTimeOffset.Now.AddMinutes(5 * ++_runs);
        var outcome = await Scheduler.Enqueue(runtime, set, when, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAllAsync(ServiceRuntime runtime, string setId)
    {
        // Two minutes after the newest run's injected clock: the ledger rows
        // this stamps must not read older than the run they follow, or the
        // next run holds every destination out as stale.
        FanOut.EnqueueAll(
            runtime, runtime.Configuration.BackupSets.Single(),
            DateTimeOffset.Now.AddMinutes((5 * _runs) + 2), userInitiated: true);
        await WaitForAsync(
            () => !runtime.Queue.IsActive(FanOut.JobIdFor(setId, "vault-a"))
                && !runtime.Queue.IsActive(FanOut.JobIdFor(setId, "vault-b")),
            Timeout);
    }

    private static List<string> SnapshotObjects(string replica) =>
        Directory.Exists(Path.Combine(replica, "snapshots"))
            ? [.. Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories)]
            : [];

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "vault-a",
                Kind = DestinationKind.LocalPath,
                Path = VaultA,
            },
            new DestinationConfiguration
            {
                Id = new string('2', 32),
                Name = "vault-b",
                Kind = DestinationKind.LocalPath,
                Path = VaultB,
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 4h",
                Retention = new RetentionConfiguration { MinGenerations = 10 },
                Destinations =
                [
                    new SetDestinationReference
                    {
                        Ref = "vault-a",
                        Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    },
                    new SetDestinationReference { Ref = "vault-b" },
                ],
                DirectShip = true,
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

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }
}
