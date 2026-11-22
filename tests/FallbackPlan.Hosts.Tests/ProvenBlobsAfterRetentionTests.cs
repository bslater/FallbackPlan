using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A backup's proof outlives retention letting the proven backup go
/// (FR-VER-002, ADR-0199 Amendment 5): a proof that ends keeps the names of
/// the blobs its backup needed at that destination, and the next backup's
/// proof asks only about the blobs none of them was, though the catalogue no
/// longer holds the backup they were proven through.
/// </summary>
/// <remarks>
/// Without them, a destination away while retention let its proven backup
/// go would be asked about every blob of the next backup again: a whole
/// archive over a peer's link, for blobs proven there already.
/// </remarks>
[TestClass]
public sealed class ProvenBlobsAfterRetentionTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task AProofThatEnds_KeepsTheNamesOfTheBlobsItsBackupNeeded()
    {
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        var proven = Row(runtime).ProvenSnapshot;
        Assert.IsNotNull(proven, "the premise: the vault is proven through the first backup");

        var kept = new ProvenBlobsStore(_harness.StateDirectory).Read(_harness.DocsSetId, "vault", proven);

        Assert.IsNotNull(kept, "the proof that ended kept nothing");
        var stagingNames = StagingBlobs(runtime).Select(key => key[(key.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);
        Assert.IsNotEmpty(kept);
        Assert.IsTrue(kept.All(stagingNames.Contains), "a kept name is no blob of the backup");
        Assert.IsNull(
            new ProvenBlobsStore(_harness.StateDirectory).Read(_harness.DocsSetId, "vault", "snapshots/d/s/another"),
            "the names kept for one backup were given for another");
    }

    [TestMethod]
    public async Task ABackupProvenBeforeRetentionLetItGo_StillSparesItsBlobs_FromTheNextBackupsProof()
    {
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        await PassAsync(runtime);
        var first = Row(runtime).ProvenSnapshot;
        Assert.IsNotNull(first, "the premise: the vault is proven through the first backup");

        var before = StagingBlobs(runtime);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var added = StagingBlobs(runtime).Except(before, StringComparer.Ordinal).Count();
        Assert.IsGreaterThan(0, added, "the premise: the second backup wrote blobs");

        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var retention);
        Assert.IsFalse(
            StagingRecords(runtime).Contains(first, StringComparer.Ordinal),
            $"the premise: retention let the first backup go:{Environment.NewLine}{string.Join(Environment.NewLine, retention.Lines)}");

        // One blob a pass, so the passes count the blobs asked about.
        FanOut.NewBackupProofBudget = 1;
        await SyncAsync(runtime);

        var row = Row(runtime);
        Assert.AreEqual(DestinationSyncState.InSync, row.State, row.LastError);
        Assert.AreEqual(
            added, row.ProvingSnapshot is null ? 1 : row.ProvingBlobsProven + row.ProvingBlobsLeft,
            "the proof asked about blobs proven there through the backup retention let go");
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
                    Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
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

    private async Task PassAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
    }

    private async Task BackUpAloneAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), at, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var answer = await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
            .ExecuteAsync(new SyncCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private string Staging(ServiceRuntime runtime) => runtime.ArchivePath(_harness.DocsSetId);

    private List<string> StagingBlobs(ServiceRuntime runtime) => [.. KeysUnder(Staging(runtime), "blobs")];

    private List<string> StagingRecords(ServiceRuntime runtime) => [.. KeysUnder(Staging(runtime), "snapshots")];

    private static IEnumerable<string> KeysUnder(string root, string plane)
    {
        var folder = Path.Combine(root, plane);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
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
