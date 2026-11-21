using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Where a destination's proof of new backups stands, on its status row
/// (FR-VER-003, contract 1.91, ADR-0199 Amendment 2): the backup it was last
/// proven through, named as the snapshot listing names it and dated by when
/// it was taken, and a proof under way counted in the backup's blobs, how
/// many are proven and how many are left, moving on as the passes do.
/// </summary>
[TestClass]
public sealed class NewBackupProofStatusTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task AProofUnderWay_IsCountedInTheBackupsBlobs_AndTheRowNamesTheBackupOnceItIsProven()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        Assert.IsNull((await RowAsync(runtime)).NewBackupProof, "a destination nothing has reached has no proof to report");

        await PassAsync(runtime);
        var newest = Assert.ContainsSingle(await SnapshotsAsync(runtime));
        var first = (await RowAsync(runtime)).NewBackupProof;
        Assert.IsNotNull(first, "a proof under way was not reported");
        Assert.IsNull(first.ProvenSnapshotId, "no backup is proven here yet");
        Assert.AreEqual(newest.SnapshotId, first.ProvingSnapshotId);
        Assert.AreEqual(newest.CapturedAt, first.ProvingCapturedAt);
        Assert.AreEqual(1, first.BlobsProven);
        Assert.IsGreaterThan(0, first.BlobsLeft ?? 0, "the premise: the backup needs more than one blob");

        // One blob a pass: each pass counts one more proven of the same
        // whole, and the proof takes as many passes as the count said.
        var proven = 1;
        var needs = proven + first.BlobsLeft!.Value;
        NewBackupProofDescriptor last;
        while (true)
        {
            Assert.IsLessThan(needs, proven, "the proof ran past the blobs it counted");
            await SyncAsync(runtime);
            last = (await RowAsync(runtime)).NewBackupProof!;
            proven++;
            if (last.ProvingSnapshotId is null)
            {
                break;
            }

            Assert.AreEqual(proven, last.BlobsProven, "a pass did not count the blob it proved");
            Assert.AreEqual(needs, last.BlobsProven + last.BlobsLeft, "the backup's blobs were counted differently pass to pass");
        }

        Assert.AreEqual(needs, proven, "the proof finished before or after the blobs it counted");
        Assert.AreEqual(newest.SnapshotId, last.ProvenSnapshotId);
        Assert.AreEqual(newest.CapturedAt, last.ProvenCapturedAt);
        Assert.IsNull(last.ProvingCapturedAt);
        Assert.IsNull(last.BlobsProven, "a finished proof still counted blobs");
        Assert.IsNull(last.BlobsLeft, "a finished proof still counted blobs");
    }

    [TestMethod]
    public async Task ANewerBackupsProof_NamesBothBackups_AndCountsOnlyTheBlobsItAdded()
    {
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        while ((await RowAsync(runtime)).NewBackupProof?.ProvenSnapshotId is null)
        {
            await SyncAsync(runtime);
        }

        var earlier = Assert.ContainsSingle(await SnapshotsAsync(runtime));
        var before = StagingBlobs(runtime);
        await BackUpAloneAsync(runtime);
        var added = StagingBlobs(runtime).Except(before, StringComparer.Ordinal).Count();
        var newer = Assert.ContainsSingle((await SnapshotsAsync(runtime)).Where(snapshot => snapshot.SnapshotId != earlier.SnapshotId));
        Assert.IsGreaterThan(1, added, "the premise: the second backup wrote more than a pass's share of blobs");

        await SyncAsync(runtime);

        var proof = (await RowAsync(runtime)).NewBackupProof!;
        Assert.AreEqual(earlier.SnapshotId, proof.ProvenSnapshotId);
        Assert.AreEqual(earlier.CapturedAt, proof.ProvenCapturedAt);
        Assert.AreEqual(newer.SnapshotId, proof.ProvingSnapshotId);
        Assert.AreEqual(newer.CapturedAt, proof.ProvingCapturedAt);
        Assert.AreEqual(1, proof.BlobsProven);
        Assert.AreEqual(added - 1, proof.BlobsLeft, "the proof counted blobs the earlier backup had needed too");
    }

    [TestMethod]
    public async Task AProofUnderWayBeforeTheLedgerCounted_StaysUncounted_RatherThanReadingShort()
    {
        // A ledger from before the counts: the proof is under way from its
        // first blob and nothing counted it. Counted from none, its whole
        // would read short for the rest of it.
        FanOut.NewBackupProofBudget = 1;
        await using var runtime = await StartAsync();
        await PassAsync(runtime);
        var row = runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;
        Assert.IsNotNull(row.ProvingSnapshot, "the premise: the backup needs more than one blob");
        runtime.DestinationSync.RecordNewBackupProof(
            _harness.DocsSetId, "vault", row.ProvenSnapshot, row.ProvingSnapshot, cursor: null, 2_000);

        await SyncAsync(runtime);

        var proof = (await RowAsync(runtime)).NewBackupProof!;
        Assert.IsNotNull(proof.ProvingSnapshotId, "the premise: the proof restarted from its first blob is still under way");
        Assert.IsNull(proof.BlobsProven, "an uncounted proof was counted from none");
        Assert.IsNull(proof.BlobsLeft, "an uncounted proof was counted from none");
    }

    [TestMethod]
    public void ABackupTheCatalogueNoLongerHolds_IsNamedByTheIdentityItsKeyCarries_Undated()
    {
        // Its retention let it go: the ledger still names it, and nothing
        // here can say when it was taken.
        var id = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var gone = Repository.Packing.MetadataStoreKeys.Snapshot(new byte[16], new byte[16], id).Value;

        var proof = ServiceCommandHandler.DescribeNewBackupProof(
            Ledger() with { ProvenSnapshot = gone }, new Dictionary<string, ServiceCommandHandler.KnownBackup>());

        Assert.IsNotNull(proof);
        Assert.AreEqual(Convert.ToHexStringLower(id), proof.ProvenSnapshotId);
        Assert.IsNull(proof.ProvenCapturedAt);
    }

    [TestMethod]
    public void ADestinationProvenThroughNoBackup_WithNoneUnderWay_ReportsNothing() =>
        Assert.IsNull(ServiceCommandHandler.DescribeNewBackupProof(
            Ledger(), new Dictionary<string, ServiceCommandHandler.KnownBackup>()));

    private DestinationSyncRecord Ledger() => new()
    {
        SetId = _harness.DocsSetId, Destination = "vault", State = DestinationSyncState.InSync, LastAttemptAt = 1_000,
    };

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

    /// <summary>Another backup, of more content than one blob holds, with no sync after it.</summary>
    private async Task BackUpAloneAsync(ServiceRuntime runtime)
    {
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        _harness.WriteSourceFile("docs/sub/d.txt", "delta");
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), _start.AddHours(1), userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new SyncCommand("docs", "vault"), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private async Task<DestinationStatusDescriptor> RowAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new GetStatusCommand(), Timeout);
        Assert.IsInstanceOfType<StatusResult>(answer, out var status, (answer as ServiceError)?.Message);
        return Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
    }

    private async Task<IReadOnlyList<SnapshotDescriptor>> SnapshotsAsync(ServiceRuntime runtime)
    {
        var answer = await Handler(runtime).ExecuteAsync(new ListSnapshotsCommand(), Timeout);
        Assert.IsInstanceOfType<SnapshotsResult>(answer, out var listing, (answer as ServiceError)?.Message);
        return listing.Snapshots;
    }

    private static ServiceCommandHandler Handler(ServiceRuntime runtime) => new(runtime, RemoteBindingState.Off);

    private List<string> StagingBlobs(ServiceRuntime runtime)
    {
        var root = runtime.ArchivePath(_harness.DocsSetId);
        var folder = Path.Combine(root, "blobs");
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
