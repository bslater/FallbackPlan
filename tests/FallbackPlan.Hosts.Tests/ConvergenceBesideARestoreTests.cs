using FallbackPlan.Agent;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A sync's convergence beside a restore of the same set (FR-GC-003,
/// FR-GC-010; ADR-0172 Amendment 4). A destination whose own policy keeps
/// less than staging drops what it no longer keeps as each sync converges
/// it, and a restore under way may read around to that copy. While the
/// service has one under way, the destination keeps its blobs, and the
/// first sync after the restore ends drops them, though nothing was
/// published between.
/// </summary>
[TestClass]
public sealed class ConvergenceBesideARestoreTests : IDisposable
{
    private const string Run = "0123456789abcdef";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Sync_WhileARestoreOfTheSetIsUnderWay_KeepsTheBlobsItsPolicyDrops_AndTheNextSyncDropsThem()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "first content");

        await using var runtime = await StartAsync();
        var set = runtime.Configuration.BackupSets.Single();

        await BackUpAsync(runtime);
        await SyncAsync(runtime, set.Id);
        var replica = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var first = Blobs(replica);
        Assert.IsNotEmpty(first);

        // The destination keeps only the newest of the day's snapshots, so
        // the first one's blobs are what its policy drops.
        _harness.WriteSourceFile("docs/a.txt", "second content");
        await BackUpAsync(runtime);

        runtime.RestoreRuns.Begin(new RestoreRunRecord
        {
            Run = Run,
            SetName = set.Name,
            SnapshotId = new string('e', 32),
            OutputDirectory = "/srv/restored",
            BeganAt = 1_000,
        });
        await SyncAsync(runtime, set.Id);

        Assert.ContainsSingle(Objects(replica, "snapshots"));
        Assert.IsEmpty(
            first.Except(Blobs(replica)),
            "a sync beside the set's restore dropped blobs the restore may read around to");

        // With the run ended, the next sync drops them, though nothing was
        // published since and its keep-set has not moved.
        runtime.RestoreRuns.Leave(Run);
        await SyncAsync(runtime, set.Id);

        Assert.IsNotEmpty(
            first.Except(Blobs(replica)),
            "the sync after the restore kept what only the restore held");
        Assert.ContainsSingle(Objects(replica, "snapshots"));
    }

    private int _runs;

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var when = DateTimeOffset.Now.AddMinutes(5 * ++_runs);
        var outcome = await Scheduler.Enqueue(runtime, set, when, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime, string setId)
    {
        // After the newest run's injected clock, so the ledger row this
        // stamps does not read older than the run it follows.
        FanOut.EnqueueAll(
            runtime, runtime.Configuration.BackupSets.Single(),
            DateTimeOffset.Now.AddMinutes((5 * _runs) + 2), userInitiated: true);
        while (runtime.Queue.IsActive(FanOut.JobIdFor(setId, "vault")))
        {
            await Task.Delay(25, Timeout);
        }
    }

    private static HashSet<string> Blobs(string replica) => [.. Objects(replica, "blobs")];

    private static List<string> Objects(string replica, string prefix) =>
        Directory.Exists(Path.Combine(replica, prefix))
            ? [.. Directory.GetFiles(Path.Combine(replica, prefix), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(replica, path))]
            : [];

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "vault",
                Kind = DestinationKind.LocalPath,
                Path = Vault,
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
                        Ref = "vault",
                        Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    },
                ],
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

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
