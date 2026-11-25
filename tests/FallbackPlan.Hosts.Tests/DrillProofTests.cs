using System.Collections.Concurrent;
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
/// of the backup proven there states nothing of it. A drill restores the
/// newest backup proven where it reads, where the destination holds it, and
/// says the newest backup there was not yet proven; where nothing is proven
/// there yet, or the destination no longer holds the backup proven there, it
/// restores the newest as before (FR-DRL-002, ADR-0199 Amendment 11). A
/// counted check says at least how many more passes it had to go at the
/// destination's share, as the status row does (FR-DRL-002, ADR-0199
/// Amendment 16).
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
    public async Task ADrillWhereTheNewestBackupIsUnderProof_SaysItWasNotYetProven_AndWhichBackupItRestored()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var first = Row(runtime).ProvenSnapshot!;
        var second = await BackUpAndSyncAsync(runtime);
        var proof = Row(runtime);
        Assert.AreEqual(second, proof.ProvingSnapshot, "the premise: the second backup's proof is under way at the vault");

        var drilled = await DrillAsync(runtime);

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        var row = Row(runtime);
        Assert.IsNotNull(row.DrillLimit, "a drill where the newest backup is not yet proven there stated nothing of it");
        Assert.Contains("The newest backup, the backup of", row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains(NewBackupProof.IdentityOf(second)[..8], row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains(
            $"was not yet proven here: its check had proven {proof.ProvingBlobsProven} of {proof.ProvingBlobsProven + proof.ProvingBlobsLeft} blobs",
            row.DrillLimit, StringComparison.Ordinal);
        var left = proof.ProvingBlobsLeft!.Value;
        Assert.Contains(
            $" blobs, with at least {left} more {(left == 1 ? "pass" : "passes")} to go;", row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains("the drill restored the newest backup proven here, the backup of", row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains(NewBackupProof.IdentityOf(first)[..8], row.DrillLimit, StringComparison.Ordinal);
        Assert.Contains("was not yet proven here", Assert.ContainsSingle(drilled.Lines), StringComparison.Ordinal);

        // Recorded where every surface that reports a drill reads it.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        Assert.AreEqual(row.DrillLimit, Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations).DrillLimit);
    }

    [TestMethod]
    public async Task ADrillWhereTheNewestBackupIsUnderProof_RestoresTheNewestBackupProvenThere()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var first = Row(runtime).ProvenSnapshot!;
        await BackUpAndSyncAsync(runtime);

        var (outcome, restored) = await DrillRecordingAsync(runtime);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.IsNotEmpty(restored, "the premise: the drill restored samples");
        Assert.IsTrue(
            restored.All(id => id == NewBackupProof.IdentityOf(first)),
            "the drill restored a backup not proven there: " + string.Join(", ", restored));
    }

    [TestMethod]
    public async Task ADrillOfTheBackupProvenThere_RestoresIt_AndStatesNothingOfItsProof()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime);
        await SyncUntilProvenAsync(runtime, second);

        var (outcome, restored) = await DrillRecordingAsync(runtime);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.IsTrue(restored.All(id => id == NewBackupProof.IdentityOf(second)), string.Join(", ", restored));
        Assert.DoesNotContain("proven here", outcome.Limit ?? string.Empty, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADrillWhereNoBackupIsProvenYet_RestoresTheNewest_AndSaysItWasNotYetProven()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var only = Assert.ContainsSingle(StagingRecords(runtime));
        Assert.IsNull(Row(runtime).ProvenSnapshot, "the premise: one blob a pass leaves the first backup's proof under way");

        var (outcome, restored) = await DrillRecordingAsync(runtime);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.IsNotEmpty(restored, "the premise: the drill restored samples");
        Assert.IsTrue(restored.All(id => id == NewBackupProof.IdentityOf(only)), string.Join(", ", restored));
        Assert.Contains("was not yet proven here", outcome.Limit ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("no backup was proven here yet", outcome.Limit ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("the drill restored", outcome.Limit ?? string.Empty, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ADrillWhoseDestinationNoLongerHoldsTheBackupProvenThere_RestoresTheNewest()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime);
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", "snapshots/gone/gone/gone", proving: null, cursor: null,
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var (outcome, restored) = await DrillRecordingAsync(runtime);

        Assert.IsNull(outcome.Failure, outcome.Failure);
        Assert.IsNotEmpty(restored, "the premise: the drill restored samples");
        Assert.IsTrue(restored.All(id => id == NewBackupProof.IdentityOf(second)), string.Join(", ", restored));
        Assert.DoesNotContain("the drill restored", outcome.Limit ?? string.Empty, StringComparison.Ordinal);
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

        // Its own check counted, with passes to go at one blob a pass.
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", first, second, cursor: null, now, blobsProven: 3, blobsLeft: 7);
        Assert.Contains(
            "was not yet proven here: its check had proven 3 of 10 blobs, with at least 7 more passes to go;",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // One pass to go.
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", first, second, cursor: null, now, blobsProven: 9, blobsLeft: 1);
        Assert.Contains(
            "was not yet proven here: its check had proven 9 of 10 blobs, with at least 1 more pass to go;",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // At the vault's own share of four blobs a pass, not the default's.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<DestinationsResult>(
            await handler.ExecuteAsync(new ListDestinationsCommand(), Timeout), out var listed);
        Assert.IsInstanceOfType<AcknowledgedResult>(await handler.ExecuteAsync(
            new UpsertDestinationCommand(Assert.ContainsSingle(listed.Destinations) with { NewBackupProofShare = 4 }), Timeout));
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", first, second, cursor: null, now, blobsProven: 3, blobsLeft: 7);
        Assert.Contains(
            "was not yet proven here: its check had proven 3 of 10 blobs, with at least 2 more passes to go;",
            await RecoveryDrillJob.NotYetProvenAsync(runtime, set, "vault", drilled, Timeout) ?? "", StringComparison.Ordinal);

        // None left to count: no passes said.
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", first, second, cursor: null, now, blobsProven: 10, blobsLeft: 0);
        Assert.Contains(
            "was not yet proven here: its check had proven 10 of 10 blobs;",
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

    /// <summary>Drills the vault through a handler that records which backup each of the drill's restores read.</summary>
    private async Task<(RecoveryDrillJob.DrillOutcome Outcome, List<string> Restored)> DrillRecordingAsync(ServiceRuntime runtime)
    {
        var recording = new RecordingService(
            new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Service) { RestoresForADrill = true });
        var outcome = await RecoveryDrillJob.RunAsync(
            runtime, recording, runtime.Configuration.BackupSets.Single(), "vault",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), budget: null, Random.Shared, Timeout);
        return (outcome, [.. recording.Restored]);
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

    /// <summary>The service, recording the backup each restore asked of it read.</summary>
    private sealed class RecordingService(IFallbackPlanService inner) : IFallbackPlanService
    {
        public ConcurrentQueue<string> Restored { get; } = new();

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            if (command is RunRestoreCommand restore)
            {
                Restored.Enqueue(restore.SnapshotId);
            }

            return inner.ExecuteAsync(command, cancellationToken);
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            inner.WatchAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}
