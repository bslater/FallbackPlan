using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A drill says when the backup it drilled was not yet proven where it read
/// it (FR-DRL-002, ADR-0199 Amendment 8): a passing drill proves the files it
/// sampled restore, not that every blob the backup needs is whole there, so
/// a drill of a backup whose proof had not ended states that as a limit,
/// with how far the proof had got and the newest backup proven there. A drill
/// of the backup proven there states nothing of it.
/// </summary>
/// <remarks>
/// One blob a pass, set before the runtime starts, so a second backup's
/// proof is under way after the pass that sent it. The installation is set
/// up, so every drill here also states the sealed-content limit.
/// </remarks>
[TestClass]
public sealed class DrillProofTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task ADrillOfABackupWhoseProofIsUnderWay_StatesItWasNotYetProvenThere()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime);
        var proof = Row(runtime);
        Assert.AreEqual(second, proof.ProvingSnapshot, "the premise: the second backup's proof is under way at the vault");

        var drilled = await DrillAsync(runtime);

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        var row = Row(runtime);
        Assert.IsNotNull(row.DrillLimit, "a drill of a backup not yet proven there stated nothing of it");
        Assert.Contains("was not yet proven here", row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains(
            $"its check had proven {proof.ProvingBlobsProven} of {proof.ProvingBlobsProven + proof.ProvingBlobsLeft} blobs",
            row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains("the newest backup proven here was the backup of", row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains(NewBackupProof.IdentityOf(second)[..8], row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains("was not yet proven here", Assert.ContainsSingle(drilled.Lines), StringComparison.Ordinal);

        // Recorded where every surface that reports a drill reads it.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        Assert.AreEqual(row.DrillLimit, Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations).DrillLimit);
    }

    [TestMethod]
    public async Task ADrillOfTheBackupProvenThere_StatesNothingOfItsProof()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime);
        await SyncUntilProvenAsync(runtime, second);

        var drilled = await DrillAsync(runtime);

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        var limit = Row(runtime).DrillLimit ?? string.Empty;
        Assert.DoesNotContain("proven here", limit, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADrillThatFails_StatesItsFailure_NotTheProof()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        await BackUpAndSyncAsync(runtime);
        foreach (var path in Directory.GetFiles(
            Path.Combine(Assert.ContainsSingle(Directory.GetDirectories(Vault)), "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            for (var at = 200; at < bytes.Length; at++)
            {
                bytes[at] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }

        var drilled = await DrillAsync(runtime);

        Assert.AreEqual(1, drilled.Failed, string.Join(" | ", drilled.Lines));
        Assert.IsNull(Row(runtime).DrillLimit, "a drill that failed stated a limit beside its failure");
    }

    [TestMethod]
    public async Task TheNote_SaysWhereTheDrilledBackupsProofStood_ForEachStandingTheLedgerKnows()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var first = Row(runtime).ProvenSnapshot!;
        var second = await BackUpAndSyncAsync(runtime);
        var set = runtime.Configuration.BackupSets.Single();
        var drilled = NewBackupProof.IdentityOf(second);
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Taken after the newest proven there, its own proof never begun or
        // taken over: not yet proven, with no check of its own to count.
        runtime.DestinationSync.RecordNewBackupProof(_harness.DocsSetId, "vault", first, proving: null, cursor: null, now);
        var after = await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout);
        Assert.IsNotNull(after, "a backup taken after the one proven there was not said unproven");
        Assert.Contains("was not yet proven here; the newest backup proven here was the backup of", after, StringComparison.Ordinal);
        Assert.DoesNotContain("its check", after, StringComparison.Ordinal);

        // Its own check failed at a blob.
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", first, second, cursor: null, now, blobsProven: 1, blobsLeft: 1, failedKey: "blobs/data/ab/cd");
        Assert.Contains(
            "was not yet proven here: its check had failed at blobs/data/ab/cd;",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // Its own check under way, begun before the ledger counted.
        runtime.DestinationSync.RecordNewBackupProof(_harness.DocsSetId, "vault", first, second, cursor: null, now);
        Assert.Contains(
            "was not yet proven here: its check was under way;",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // No backup proven there at all.
        runtime.DestinationSync.RecordNewBackupProof(_harness.DocsSetId, "vault", proven: null, second, cursor: null, now);
        Assert.EndsWith(
            "; no backup was proven here yet.",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // The backup proven there, and one the set's catalogue does not hold.
        runtime.DestinationSync.RecordNewBackupProof(_harness.DocsSetId, "vault", second, proving: null, cursor: null, now);
        Assert.IsNull(await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout));
        Assert.IsNull(await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", new string('0', 32), Timeout));
    }

    private async Task ProveFirstAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        await SyncUntilProvenAsync(runtime, Assert.ContainsSingle(StagingRecords(runtime)));
    }

    /// <summary>Another backup, of one more file, sent to the vault by one sync: its proof is under way after it.</summary>
    private async Task<string> BackUpAndSyncAsync(ServiceRuntime runtime)
    {
        var before = StagingRecords(runtime);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), _start.AddHours(1), userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
        var taken = Assert.ContainsSingle(StagingRecords(runtime).Except(before, StringComparer.Ordinal));
        await SyncAsync(runtime);
        return taken;
    }

    private async Task SyncUntilProvenAsync(ServiceRuntime runtime, string backup)
    {
        for (var passes = 0; !string.Equals(Row(runtime).ProvenSnapshot, backup, StringComparison.Ordinal); passes++)
        {
            Assert.IsLessThan(20, passes, "the backup's proof never finished");
            await SyncAsync(runtime);
        }
    }

    private async Task<DrillResult> DrillAsync(ServiceRuntime runtime)
    {
        var answer = await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
            .ExecuteAsync(new RunDrillCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<DrillResult>(answer, out var drilled, (answer as ServiceError)?.Message);
        return drilled;
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        Directory.CreateDirectory(Vault);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var answer = await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
            .ExecuteAsync(new SyncCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private List<string> StagingRecords(ServiceRuntime runtime)
    {
        var root = runtime.ArchivePath(_harness.DocsSetId);
        var folder = Path.Combine(root, "snapshots");
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))]
            : [];
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}
