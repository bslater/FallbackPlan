using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The retention-with-trimming drill against a direct-ship set — the gate
/// ADR-0046 named before direct-ship could become the default: aged
/// snapshots expire through the full cycle (tombstone, grace, sweep) with
/// the archive living at the destinations, the trim reaches the destination
/// replica through the convergence path, and what remains is still a whole,
/// restorable repository. Establishes the retention half of FR-DEST-015 and
/// the direct-ship shape of FR-GC-006 and FR-GC-010.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipRetentionTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    private string VaultA => Path.Combine(_harness.WorkPath, "vault-a");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task Retention_AgainstADirectShipSet_TrimsTheDestinationAndKeepsItRestorable()
    {
        Directory.CreateDirectory(VaultA);
        WriteConfiguration();
        _harness.WriteSourceFile("docs/a.txt", "day one content");

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var setId = _harness.DocsSetId;

        // Three snapshots, all shipped directly. In the service path a
        // snapshot's CapturedAt is the real clock, so all of these share one
        // daily bucket: KeepDaily=1 + MinGenerations=1 keeps the newest and
        // expires the rest — exactly the pressure the drill wants.
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/a.txt", "second content");
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/a.txt", "third content");
        await BackUpAsync(runtime);

        var replica = Assert.ContainsSingle(Directory.GetDirectories(VaultA));
        Assert.HasCount(3, SnapshotObjects(replica));
        var blobsAtFull = BlobObjects(replica).Count;

        // The full cycle, twice over: apply tombstones the expired, a later
        // publication is the grace (FR-GC-006), the next apply sweeps — and
        // the sink fans each sweep delete to the destinations, the gate
        // having already established every entitled holder dropped it
        // (FR-GC-010). Before that fan, destinations only ever grew.
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true), Timeout));
        _harness.WriteSourceFile("docs/a.txt", "fourth content");
        await BackUpAsync(runtime);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true), Timeout));
        _harness.WriteSourceFile("docs/a.txt", "fifth content");
        await BackUpAsync(runtime);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true), Timeout));

        // Belt and braces: an explicit sync pass, so the assertion below is
        // about the converged steady state, not a race with the last apply.
        FanOut.EnqueueAll(runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
        await WaitForAsync(() => !runtime.Queue.IsActive(FanOut.JobIdFor(setId, "vault-a")), Timeout);

        // Five runs happened; the converged destination holds exactly its
        // policy's keep-set — one snapshot (FR-GC-010's designed steady
        // state; the tombstoned-in-grace record is the metadata plane's
        // concern until swept). The trimmed majority is gone FROM THE
        // DESTINATION — the whole point of the drill: before the sink
        // fanned its deletes, this count only ever grew.
        Assert.HasCount(1, SnapshotObjects(replica));
        Assert.IsLessThan(
            blobsAtFull + 3, BlobObjects(replica).Count,
            "five runs' content should not all still sit at the destination — the trim did not converge");

        // What remains is a working archive: the newest snapshot restores
        // through the sink, whose blob reads answer from the destination.
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.OrderByDescending(snapshot => snapshot.CapturedAt).First();
        var output = Path.Combine(_harness.WorkPath, "restored");
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(new RunRestoreCommand(newest.SnapshotId, null, output), Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var recovered = Assert.ContainsSingle(Directory.GetFiles(output, "a.txt", SearchOption.AllDirectories));
        Assert.Contains("fifth content", await File.ReadAllTextAsync(recovered, Timeout), StringComparison.Ordinal);
    }

    private int _runs;

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var when = DateTimeOffset.Now.AddMinutes(5 * ++_runs);
        var outcome = await Scheduler.Enqueue(runtime, set, when, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private static List<string> SnapshotObjects(string replica) =>
        [.. Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories)];

    private static List<string> BlobObjects(string replica) =>
        Directory.Exists(Path.Combine(replica, "blobs"))
            ? [.. Directory.GetFiles(Path.Combine(replica, "blobs"), "*", SearchOption.AllDirectories)]
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
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 4h",
                Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                Destinations = [new SetDestinationReference { Ref = "vault-a" }],
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
