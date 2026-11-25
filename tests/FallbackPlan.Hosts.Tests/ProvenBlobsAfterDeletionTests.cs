using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A backup's proof outlives the catalogue letting the proven backup go
/// (FR-VER-002, ADR-0199 Amendment 5): a proof that ends keeps the names of
/// the blobs its backup needed at that destination, and the next backup's
/// proof asks only about the blobs none of them was, though the catalogue no
/// longer holds the backup they were proven through. A catalogue rebuilt
/// without that backup is answered the same way, and where no names were
/// kept, as for a destination proven before any were, the catalogue's own
/// list of that backup's blobs still spares them (ADR-0199 Amendment 12).
/// </summary>
/// <remarks>
/// Retention's expiry keeps a backup's row in the catalogue, so the case is a
/// person's deletion of the backup a destination was proven through, or a
/// catalogue rebuilt without it. Without the names kept, the next backup's
/// proof would ask again about every blob it shares with that backup: at a
/// peer, an archive's worth over the peer's link, for blobs proven there
/// already.
/// </remarks>
[TestClass]
public sealed class ProvenBlobsAfterDeletionTests : IAsyncDisposable
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
    public async Task ABackupAPersonDeleted_StillSparesTheBlobsItWasProvenThrough_FromTheNextBackupsProof()
    {
        // One blob a pass, so the pass the deletion converges the vault with
        // leaves the second backup's proof under way, the first still named
        // as the backup proven there when the catalogue lets it go.
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        await PassAsync(runtime);
        for (var passes = 0; Row(runtime).ProvenSnapshot is null; passes++)
        {
            Assert.IsLessThan(10, passes, "the first backup's proof never finished");
            await SyncAsync(runtime);
        }

        var first = Row(runtime).ProvenSnapshot;
        var firstBlobs = StagingBlobs(runtime).Select(NameOf).ToHashSet(StringComparer.Ordinal);
        var firstId = Assert.ContainsSingle(await SnapshotsAsync(handler)).SnapshotId;
        var records = StagingRecords(runtime);

        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var second = Assert.ContainsSingle(StagingRecords(runtime).Except(records, StringComparer.Ordinal));

        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await handler.ExecuteAsync(
                new DeleteSnapshotsCommand(_harness.DocsSetId, [firstId], Apply: true, ReclaimGrant: grant), Timeout),
            out var deleted);
        Assert.AreEqual("deleted", Assert.ContainsSingle(deleted.Snapshots).State, string.Join(" | ", deleted.Lines));
        Assert.HasCount(1, await SnapshotsAsync(handler), "the premise: the catalogue let the first backup go");
        var row = Row(runtime);
        Assert.AreEqual(first, row.ProvenSnapshot, "the premise: the vault is still proven through the first backup");
        Assert.AreEqual(second, row.ProvingSnapshot, "the premise: the second backup's proof is under way");

        // What the second backup's proof owes, from its first blob.
        var set = runtime.Configuration.BackupSets.Single();
        var request = NewBackupProof.Request(
            runtime, set, "vault", await runtime.ArchiveForAsync(set, Timeout), second,
            row with { ProvingSnapshot = null, ProvingCursor = null }, budget: 256);

        Assert.IsNotNull(request);
        Assert.IsNotEmpty(request.Names, "the premise: the second backup added blobs");
        var asked = request.Names.Where(firstBlobs.Contains).ToList();
        Assert.IsEmpty(asked, $"the proof asks again about blobs proven there through the deleted backup: {string.Join(", ", asked)}");
    }

    [TestMethod]
    public async Task ACatalogueRebuiltWithoutTheBackupProvenThere_StillSparesItsBlobs_FromTheNextBackupsProof()
    {
        FanOut.NewBackupProofBudget = 1;
        string first;
        string second;
        string staging;
        HashSet<string> firstBlobs;
        await using (var runtime = await StartAsync())
        {
            await PassAsync(runtime);
            for (var passes = 0; Row(runtime).ProvenSnapshot is null; passes++)
            {
                Assert.IsLessThan(10, passes, "the first backup's proof never finished");
                await SyncAsync(runtime);
            }

            first = Row(runtime).ProvenSnapshot!;
            firstBlobs = StagingBlobs(runtime).Select(NameOf).ToHashSet(StringComparer.Ordinal);
            var records = StagingRecords(runtime);
            _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
            await BackUpAloneAsync(runtime, _start.AddHours(1));
            second = Assert.ContainsSingle(StagingRecords(runtime).Except(records, StringComparer.Ordinal));
            staging = Staging(runtime);
        }

        // The first backup's record is gone, and the catalogue with it, so the
        // set opens rebuilding its catalogue from what its archive still holds.
        File.Delete(Path.Combine(staging, first));
        foreach (var catalogue in Directory.GetFiles(_harness.StateDirectory, "catalogue-*"))
        {
            File.Delete(catalogue);
        }

        await using var again = await StartAsync();
        var handler = new ServiceCommandHandler(again, RemoteBindingState.Off);
        Assert.AreEqual(
            NewBackupProof.IdentityOf(second), Assert.ContainsSingle(await SnapshotsAsync(handler)).SnapshotId,
            "the premise: the catalogue was rebuilt without the first backup");
        var row = Row(again);
        Assert.AreEqual(first, row.ProvenSnapshot, "the premise: the vault is still proven through the first backup");

        var set = again.Configuration.BackupSets.Single();
        var request = NewBackupProof.Request(
            again, set, "vault", await again.ArchiveForAsync(set, Timeout), second,
            row with { ProvingSnapshot = null, ProvingCursor = null }, budget: 256);

        Assert.IsNotNull(request);
        Assert.IsNotEmpty(request.Names, "the premise: the second backup added blobs");
        var asked = request.Names.Where(firstBlobs.Contains).ToList();
        Assert.IsEmpty(asked, $"the proof asks again about blobs proven there through a backup the catalogue lost: {string.Join(", ", asked)}");
    }

    [TestMethod]
    public async Task WhereNoNamesWereKept_TheCataloguesListOfTheBackupProvenThere_StillSparesItsBlobs()
    {
        // A destination proven before names were kept, or whose file does
        // not read: the catalogue still holds the backup proven there.
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        for (var passes = 0; Row(runtime).ProvenSnapshot is null; passes++)
        {
            Assert.IsLessThan(10, passes, "the first backup's proof never finished");
            await SyncAsync(runtime);
        }

        var firstBlobs = StagingBlobs(runtime).Select(NameOf).ToHashSet(StringComparer.Ordinal);
        var records = StagingRecords(runtime);
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        await BackUpAloneAsync(runtime, _start.AddHours(1));
        var second = Assert.ContainsSingle(StagingRecords(runtime).Except(records, StringComparer.Ordinal));
        Directory.Delete(Path.Combine(_harness.StateDirectory, "proven-blobs"), recursive: true);

        var set = runtime.Configuration.BackupSets.Single();
        var row = Row(runtime);
        var request = NewBackupProof.Request(
            runtime, set, "vault", await runtime.ArchiveForAsync(set, Timeout), second,
            row with { ProvingSnapshot = null, ProvingCursor = null }, budget: 256);

        Assert.IsNotNull(request);
        Assert.IsNotEmpty(request.Names, "the premise: the second backup added blobs");
        var asked = request.Names.Where(firstBlobs.Contains).ToList();
        Assert.IsEmpty(asked, $"the proof asks again about blobs the catalogue lists for the backup proven there: {string.Join(", ", asked)}");
    }

    private async Task<IReadOnlyList<SnapshotDescriptor>> SnapshotsAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listing);
        return listing.Snapshots;
    }

    private static string NameOf(string key) => key[(key.LastIndexOf('/') + 1)..];

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
