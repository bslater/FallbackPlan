using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A sync records a destination in sync only once the destination holds the
/// newest backup whole (FR-DEST-004, ADR-0198 Amendment 2): every blob a
/// restore of that backup reads, by what the destination listed before the
/// pass and acknowledged during it. A copy can only carry what its source
/// lists, so a source short of a blob leaves the destination short of it too,
/// and the pass records that rather than a watermark that would let staging
/// let the backup go.
/// </summary>
/// <remarks>
/// Each case takes one blob of the newest backup away from what the sync
/// reads, never from the destination, and asks what the ledger then says of
/// the destination.
/// </remarks>
[TestClass]
public sealed class InSyncClosureTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly string _peersRoot = Path.Combine(Path.GetTempPath(), "fbp-in-sync-closure", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly List<LoopbackPeer> _peers = [];

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string SecondVault => Path.Combine(_harness.WorkPath, "vault-two");

    [TestMethod]
    [DataRow("data")]
    [DataRow("meta")]
    public async Task AStagingSetsPath_TheCopyLeftShortOfABlob_IsFailed_NamingIt_AndKeepsItsWatermark(string blobClass)
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, LocalPath("vault", Vault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var before = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.InSync, before.State, "the premise: the path took the first backup");

        await BackUpAloneAsync(runtime, start.AddHours(1));
        var taken = TakeAway(StagingBlobs(runtime).Except(VaultBlobs(Vault), StringComparer.Ordinal), blobClass, Staging(runtime));
        await SyncAsync(runtime, "vault");

        var vault = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, vault.State, vault.LastError);
        Assert.Contains(taken.Name, vault.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(before.SyncedSequence, vault.SyncedSequence, "the watermark moved past a backup the path does not hold");
        Assert.AreEqual(before.LastSuccessAt, vault.LastSuccessAt, "a pass that left the path short was recorded a success");
    }

    [TestMethod]
    public async Task AStagingSetsPath_IsInSync_OnceTheBlobIsBackWhereTheSyncReadsIt()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, LocalPath("vault", Vault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        await BackUpAloneAsync(runtime, start.AddHours(1));
        var taken = TakeAway(StagingBlobs(runtime).Except(VaultBlobs(Vault), StringComparer.Ordinal), "data", Staging(runtime));
        await SyncAsync(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, Row(runtime, "vault").State, "the premise: the path was left short");

        taken.Restore();
        await SyncAsync(runtime, "vault");

        var vault = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.InSync, vault.State, vault.LastError);
        Assert.Contains(taken.Key, VaultBlobs(Vault));
    }

    [TestMethod]
    public async Task ADirectShipSetsPath_CopiedFromAWholeCopyShortOfABlob_IsFailed_NamingIt()
    {
        // The second path missed the second backup, and the first, which the
        // ledger names whole, has since lost one of its blobs: the sync reads
        // the first, and can carry only what it holds.
        Directory.CreateDirectory(Vault);
        Directory.CreateDirectory(SecondVault);
        WriteConfiguration(directShip: true, LocalPath("vault", Vault), LocalPath("vault-two", SecondVault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime, "vault-two").State, "the premise: both paths took the first backup");

        var away = SecondVault + "-away";
        Directory.Move(SecondVault, away);
        await BackUpAloneAsync(runtime, start.AddHours(1));
        Directory.Move(away, SecondVault);
        var taken = TakeAway(VaultBlobs(Vault).Except(VaultBlobs(SecondVault), StringComparer.Ordinal), "data", Replica(Vault));
        await SyncAsync(runtime, "vault-two");

        var second = Row(runtime, "vault-two");
        Assert.AreEqual(DestinationSyncState.Failed, second.State, second.LastError);
        Assert.Contains(taken.Name, second.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStagingSetsPeer_ThePushLeftShortOfABlob_IsFailed_NamingIt_AndKeepsItsWatermark()
    {
        var friend = StartPeer("friend");
        WriteConfiguration(directShip: false, Peer(friend));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var before = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, before.State, "the premise: the peer took the first backup");

        await BackUpAloneAsync(runtime, start.AddHours(1));
        var taken = TakeAway(StagingBlobs(runtime).Except(friend.Blobs(), StringComparer.Ordinal), "data", Staging(runtime));
        await SyncAsync(runtime, "friend");

        var row = Row(runtime, "friend");
        Assert.AreEqual(DestinationSyncState.Failed, row.State, row.LastError);
        Assert.Contains(taken.Name, row.LastError!, StringComparison.Ordinal);
        Assert.AreEqual(before.SyncedSequence, row.SyncedSequence, "the watermark moved past a backup the peer does not hold");
    }

    [TestMethod]
    public async Task APathThatMissedTheNewestBackup_APersonDeleted_IsInSync_HoldingNoneOfIt()
    {
        // The path was away for the second backup, and a person asked for
        // that backup to go before it came back. Its keep-set leaves the
        // backup out, so the path is owed nothing of it.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: false, LocalPath("vault", Vault));
        WriteFiles();

        await using var runtime = await StartAsync();
        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);

        var away = Vault + "-away";
        Directory.Move(Vault, away);
        await BackUpAloneAsync(runtime, start.AddHours(1));
        var unique = StagingBlobs(runtime).Except(VaultBlobs(away), StringComparer.Ordinal).ToList();
        Assert.IsNotEmpty(unique, "the premise: the second backup wrote a blob the path lacks");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!.SnapshotId;
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await handler.ExecuteAsync(new DeleteSnapshotsCommand(_harness.DocsSetId, [newest], Apply: true, ReclaimGrant: grant), Timeout),
            out var requested);
        Assert.AreEqual("pending", Assert.ContainsSingle(requested.Snapshots).State, "the premise: the deletion waits for the path");

        Directory.Move(away, Vault);
        await SyncAsync(runtime, "vault");

        var vault = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.InSync, vault.State, vault.LastError);
        Assert.IsEmpty(unique.Intersect(VaultBlobs(Vault), StringComparer.Ordinal), "the path was handed the deleted backup's blobs");
    }

    private static DestinationConfiguration Peer(LoopbackPeer peer) => new()
    {
        Id = new string('1', 32),
        Name = peer.Name,
        Kind = DestinationKind.Peer,
        Fingerprint = peer.Fingerprint,
        Endpoint = $"{peer.Endpoint.Address}:{peer.Endpoint.Port}",
    };

    private static DestinationConfiguration LocalPath(string name, string path) => new()
    {
        Id = name == "vault" ? new string('2', 32) : new string('3', 32),
        Name = name,
        Kind = DestinationKind.LocalPath,
        Path = path,
    };

    /// <summary>
    /// Moves one blob of <paramref name="blobClass"/> from among
    /// <paramref name="candidates"/> out of <paramref name="root"/>, the store
    /// the sync reads, keeping it to be put back.
    /// </summary>
    private TakenBlob TakeAway(IEnumerable<string> candidates, string blobClass, string root)
    {
        var key = candidates
            .Where(candidate => candidate.StartsWith($"blobs/{blobClass}/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new AssertFailedException($"the premise: the newest backup wrote a {blobClass} blob the destination lacks");
        var path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
        var aside = Path.Combine(_harness.WorkPath, "taken-" + Path.GetFileName(path));
        File.Move(path, aside);
        return new TakenBlob(key, path, aside);
    }

    private void WriteConfiguration(bool directShip, params DestinationConfiguration[] destinations) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = [.. destinations],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [.. destinations.Select(destination => new SetDestinationReference { Ref = destination.Name })],
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private void WriteFiles()
    {
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
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

    private LoopbackPeer StartPeer(string name)
    {
        var peer = LoopbackPeer.Start(name, Path.Combine(_peersRoot, name));
        _peers.Add(peer);
        PeerGrantStore.Open(_harness.StateDirectory).Pin(
            new PeerGrant(peer.Identity, name, PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        peer.Admit(_harness.StateDirectory);
        return peer;
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    /// <summary>A second backup, of one more file, with no sync after it.</summary>
    private async Task BackUpAloneAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        _harness.WriteSourceFile("docs/c.txt", new string('c', 30_000));
        var ran = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), at, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", ran.Outcome, ran.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime, string destination)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(new SyncCommand("docs", destination), Timeout);
        Assert.IsInstanceOfType<SyncResult>(answer, (answer as ServiceError)?.Message);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime, string name) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, name) ?? throw new AssertFailedException($"no ledger row for '{name}'");

    private string Staging(ServiceRuntime runtime) => runtime.ArchivePath(_harness.DocsSetId);

    private List<string> StagingBlobs(ServiceRuntime runtime) => [.. BlobsUnder(Staging(runtime))];

    /// <summary>The one replica directory a local path holds for the set.</summary>
    private static string Replica(string vault) => Assert.ContainsSingle(Directory.GetDirectories(vault));

    private static List<string> VaultBlobs(string vault) =>
        Directory.Exists(vault) ? [.. Directory.GetDirectories(vault).SelectMany(BlobsUnder)] : [];

    private static IEnumerable<string> BlobsUnder(string replica)
    {
        var blobs = Path.Combine(replica, "blobs");
        return Directory.Exists(blobs)
            ? Directory.GetFiles(blobs, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(replica, path).Replace('\\', '/'))
            : [];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers)
        {
            await peer.DisposeAsync();
        }

        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            if (Directory.Exists(_peersRoot))
            {
                Directory.Delete(_peersRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    /// <summary>A blob moved aside from where the sync reads it.</summary>
    private sealed record TakenBlob(string Key, string From, string Aside)
    {
        /// <summary>The blob's keyed store name: the last segment of its key.</summary>
        public string Name => Key[(Key.LastIndexOf('/') + 1)..];

        public void Restore() => File.Move(Aside, From);
    }
}
