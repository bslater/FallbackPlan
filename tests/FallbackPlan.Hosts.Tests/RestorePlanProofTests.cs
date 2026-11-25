using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore's plan read through a destination says when the backup it
/// restores is not proven there (FR-RST-003, contract 1.93, ADR-0199
/// Amendment 6): the backup under proof there, and any backup taken after the
/// newest proven there, named beside where that destination's proof stands.
/// A backup proven there, one taken before it, and a plan read from the set's
/// own archive are said nothing of.
/// </summary>
/// <remarks>
/// One blob a pass, set before the runtime starts, so a second backup's
/// proof is under way after the pass that sent it.
/// </remarks>
[TestClass]
public sealed class RestorePlanProofTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task ABackupWhoseProofIsUnderWayAtTheDestination_IsSaidNotProvenThere()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var first = await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime, "docs/c.txt", 1);
        var row = Row(runtime);
        Assert.AreEqual(second, row.ProvingSnapshot, "the premise: the second backup's proof is under way at the vault");

        var plan = await PlanAsync(runtime, "vault", NewBackupProof.IdentityOf(second));

        Assert.IsNotNull(plan.NotProvenAt, "a backup under proof at the vault was planned through it unremarked");
        Assert.AreEqual("vault", plan.NotProvenAt.Destination);
        var proof = plan.NotProvenAt.Proof;
        Assert.IsNotNull(proof);
        Assert.AreEqual(NewBackupProof.IdentityOf(first), proof.ProvenSnapshotId);
        Assert.IsNotNull(proof.ProvenCapturedAt, "the backup proven there is one the destination holds, and is dated");
        Assert.AreEqual(NewBackupProof.IdentityOf(second), proof.ProvingSnapshotId);
        Assert.AreEqual(row.ProvingBlobsProven, proof.BlobsProven);
        Assert.AreEqual(row.ProvingBlobsLeft, proof.BlobsLeft);
        Assert.AreEqual(row.ProvingBlobsLeft, proof.PassesLeft, "one blob a pass: as many passes left as blobs");
    }

    [TestMethod]
    public async Task TheBackupTheDestinationIsProvenThrough_IsSaidNothingOf()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var first = await ProveFirstAsync(runtime);
        await BackUpAndSyncAsync(runtime, "docs/c.txt", 1);

        var plan = await PlanAsync(runtime, "vault", NewBackupProof.IdentityOf(first));

        Assert.IsNull(plan.NotProvenAt, "the backup the vault is proven through was said not proven there");
    }

    [TestMethod]
    public async Task APlanReadFromTheSetsOwnArchive_SaysNothingOfADestinationsProof()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime, "docs/c.txt", 1);

        var plan = await PlanAsync(runtime, destination: null, NewBackupProof.IdentityOf(second));

        Assert.IsNull(plan.NotProvenAt, "a plan read from staging spoke of a destination it does not read");
    }

    [TestMethod]
    public async Task ABackupTakenAfterTheProvenOne_WhoseProofANewerOneTookOver_IsSaidNotProvenThere()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var first = await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime, "docs/c.txt", 1);
        var third = await BackUpAndSyncAsync(runtime, "docs/d.txt", 2);
        Assert.AreEqual(third, Row(runtime).ProvingSnapshot, "the premise: the third backup's proof took the second's over");
        Assert.AreEqual(first, Row(runtime).ProvenSnapshot, "the premise: the vault is proven through the first backup alone");

        var plan = await PlanAsync(runtime, "vault", NewBackupProof.IdentityOf(second));

        Assert.IsNotNull(plan.NotProvenAt, "a backup taken after the one proven there, and never proven, was planned unremarked");
        Assert.AreEqual("vault", plan.NotProvenAt.Destination);
        Assert.AreEqual(NewBackupProof.IdentityOf(first), plan.NotProvenAt.Proof?.ProvenSnapshotId);
        Assert.AreEqual(NewBackupProof.IdentityOf(third), plan.NotProvenAt.Proof?.ProvingSnapshotId);

        // The third backup's proof owes the blobs the second added too, so
        // more than one is left: one blob a pass counts as many passes, where
        // the default share would count one.
        var proof = plan.NotProvenAt.Proof!;
        Assert.IsGreaterThan(1, proof.BlobsLeft ?? 0, "the premise: more than one blob is left to prove");
        Assert.AreEqual(proof.BlobsLeft, proof.PassesLeft, "the plan's passes left were not counted at the vault's share");
    }

    [TestMethod]
    public async Task ABackupTakenBeforeTheProvenOne_IsSaidNothingOf()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var first = await ProveFirstAsync(runtime);
        var second = await BackUpAndSyncAsync(runtime, "docs/c.txt", 1);
        await SyncUntilProvenAsync(runtime, second);

        var plan = await PlanAsync(runtime, "vault", NewBackupProof.IdentityOf(first));

        Assert.IsNull(plan.NotProvenAt, "a backup older than the one proven there was said not proven there");
    }

    [TestMethod]
    public void TheRule_SaysNotProven_OnlyOfABackupTheLedgerKnowsIsNot()
    {
        const string Earlier = "snapshots/d/s/earlier";
        const string Proven = "snapshots/d/s/proven";
        const string Proving = "snapshots/d/s/proving";
        const string Later = "snapshots/d/s/later";
        var ledger = Ledger(Proven, Proving);

        // Nothing recorded: a machine set up again to restore holds no
        // ledger that knew, so nothing is said.
        Assert.IsFalse(NewBackupProof.IsNotProven(null, Later, 3_000, 1_000));
        Assert.IsFalse(NewBackupProof.IsNotProven(Ledger(null, null), Later, 3_000, 1_000));

        Assert.IsFalse(NewBackupProof.IsNotProven(ledger, Proven, 1_000, 1_000), "the backup proven there");
        Assert.IsTrue(NewBackupProof.IsNotProven(ledger, Proving, 2_000, 1_000), "the backup under proof there");
        Assert.IsTrue(NewBackupProof.IsNotProven(ledger, Later, 3_000, 1_000), "a backup taken after the proven one");
        Assert.IsFalse(NewBackupProof.IsNotProven(ledger, Earlier, 500, 1_000), "a backup taken before the proven one");

        // Taken at the same instant as the proven one is not after it.
        Assert.IsFalse(NewBackupProof.IsNotProven(ledger, Earlier, 1_000, 1_000));

        // Where the proven backup is not held to date it, an order cannot be
        // told, and nothing is said.
        Assert.IsFalse(NewBackupProof.IsNotProven(ledger, Later, 3_000, provenCapturedAt: null));

        // No backup proven there yet: any backup but none is not proven there.
        Assert.IsTrue(NewBackupProof.IsNotProven(Ledger(null, Proving), Earlier, 500, provenCapturedAt: null));
        Assert.IsTrue(NewBackupProof.IsNotProven(Ledger(null, Proving), Proving, 2_000, provenCapturedAt: null));
    }

    private static DestinationSyncRecord Ledger(string? proven, string? proving) => new()
    {
        SetId = new string('a', 32),
        Destination = "vault",
        State = DestinationSyncState.InSync,
        LastAttemptAt = 0,
        ProvenSnapshot = proven,
        ProvingSnapshot = proving,
    };

    /// <summary>The first backup, taken and proven at the vault over as many passes as it needs.</summary>
    private async Task<string> ProveFirstAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        var first = Assert.ContainsSingle(StagingRecords(runtime));
        await SyncUntilProvenAsync(runtime, first);
        return first;
    }

    /// <summary>Another backup, of one more file, sent to the vault by one sync: its proof is under way after it.</summary>
    private async Task<string> BackUpAndSyncAsync(ServiceRuntime runtime, string file, int hours)
    {
        var before = StagingRecords(runtime);
        _harness.WriteSourceFile(file, new string((char)('c' + hours), 30_000));
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), _start.AddHours(hours), userInitiated: true)
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

    private async Task<RestorePlanResult> PlanAsync(ServiceRuntime runtime, string? destination, string snapshotId)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", destination, Timeout);
        Assert.IsTrue(
            source.Snapshots.Any(snapshot => snapshot.SnapshotId == snapshotId),
            "the premise: the source holds the backup planned");
        var answer = await handler.ExecuteAsync(new PlanRestoreCommand(snapshotId, Path: null, Source: source.SourceId), Timeout);
        Assert.IsInstanceOfType<RestorePlanResult>(answer, out var plan, (answer as ServiceError)?.Message);
        return plan;
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
