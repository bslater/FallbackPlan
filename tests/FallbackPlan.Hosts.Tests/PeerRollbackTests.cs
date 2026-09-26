using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The peer as the rollback witness
/// ([ADR-0062](../../docs/adr/0062-the-destination-is-the-rollback-witness.md)
/// Amendment 1; FR-DEST-018, NFR-SEC-005): a whole state directory restored
/// from an older copy is noticed from the inventory a peer declares at the
/// start of every push — which already names every journal key it holds —
/// so the witness costs no second session. Does not establish FR-DRL-001.
/// </summary>
/// <remarks>
/// <para>
/// The harm on the peer path is not the one the local path has. A peer
/// keeps what the source no longer lists (ADR-0034 §6), so a rolled-back
/// keep-set cannot condemn the newer history's metadata — but a
/// direct-ship set lists its blobs through the destinations themselves, so
/// the newer history's blobs ARE listed, and a granted convergence run
/// would drop them while keeping the manifests that point at them. And
/// the next backup hands out sequence numbers the peer already holds under
/// different bytes: the push skips a key the inventory declares, so the
/// replica's journal silently diverges from the source's.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerRollbackTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-rollback", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);
    private string OlderState => Path.Combine(_harness.WorkPath, "older-state");
    private string OlderArchives => Path.Combine(_harness.WorkPath, "older-archives");
    private string NoticeKey => $"destination-ahead:{_harness.DocsSetId}:friend";

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;

    [TestMethod]
    public async Task ASyncPass_AfterARollback_NoticesFromThePeersInventory_HealsAndMovesTheWriter()
    {
        var replica = await TwoBackupsThenRollBackAsync(directShip: true);
        var before = ReplicaKeys(replica);
        var sequenceBefore = await SequenceFileAsync();

        await using var runtime = await StartAsync();
        await SyncAsync(runtime);

        // Said, from the inventory alone: no retrieval session was needed to
        // learn that the peer attests a sequence this machine never handed out.
        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
        Assert.Contains("restored from an older copy", notice.Message, StringComparison.Ordinal);
        Assert.AreNotEqual(sequenceBefore, await SequenceFileAsync(), "the sequence file must move past the peer's head");
        Assert.IsEmpty(before.Except(ReplicaKeys(replica), StringComparer.Ordinal), "the pass must delete nothing at the peer");

        // Healed over the retrieval session: every metadata key the peer
        // holds is back in the set's metadata store, and the catalogue lists
        // both backups.
        Assert.Contains("copied back", notice.Message, StringComparison.Ordinal);
        foreach (var key in before.Where(IsMetadataKey))
        {
            Assert.IsTrue(File.Exists(Path.Combine(MetadataRoot, key)), $"{key} was not copied back from the peer");
        }

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(2, listed.Snapshots);

        // The proof the witness was the right number: a third backup ships
        // journal records under sequences the peer has never seen, and every
        // journal record this machine holds is at the peer byte for byte.
        // Without the witness the run re-uses a sequence the peer already
        // holds, the push skips the key it declares, and the replica's
        // journal quietly stops matching the source's.
        _harness.WriteSourceFile("docs/a.txt", "third content");
        await BackUpAsync(runtime);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var relisted);
        Assert.HasCount(3, relisted.Snapshots);
        await AssertJournalsAgreeAsync(replica);
    }

    [TestMethod]
    public async Task AGrantedConvergence_AfterARollback_DropsNothingAtThePeer()
    {
        // The convergence run is the one thing on this path that deletes,
        // and it runs whenever the operator applies retention — including
        // right after restoring the machine, before any pass has healed it.
        // A peer only ever drops keys the source lists (ADR-0034 §6), so the
        // rolled-back keep-set cannot reach the newer history's own keys;
        // what it CAN reach is a key the older history owns and the newer
        // history shares. Three backups — A, B, then A again, which dedups
        // against the first — and a rollback to after the second: the
        // rolled-back policy keeps B's closure alone, A's blob is listed and
        // not kept, and the third backup at the peer needs it. Listed by a
        // local sibling: outside a run the sink reads blobs from local
        // paths alone, so a peer-only set never exposes a blob to its own
        // convergence and the mixed set is where the guard is load-bearing.
        var replica = await ThreeBackupsRevertingThenRollBackAsync();
        var before = ReplicaKeys(replica);
        Assert.IsTrue(before.Any(key => key.StartsWith("blobs/data/", StringComparison.Ordinal)));

        await using var runtime = await StartAsync();
        await ApplyRetentionAsync(runtime);

        Assert.IsEmpty(before.Except(ReplicaKeys(replica), StringComparer.Ordinal), "the convergence must drop nothing at the peer");
        Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
    }

    /// <summary>
    /// Backups of A, then B, then A again — the third reusing the first's
    /// segment under the device dedup domain, so it ships no new data blob —
    /// with the state directory copied aside after the second and put back
    /// after the third. The peer holds all three; the machine knows two, and
    /// its keep-newest policy no longer wants the first.
    /// </summary>
    private async Task<string> ThreeBackupsRevertingThenRollBackAsync()
    {
        Directory.CreateDirectory(Vault);
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, directShip: true, withVault: true);
        _harness.WriteSourceFile("docs/a.txt", new string('a', 20_000) + "the first content");

        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime);
            _harness.WriteSourceFile("docs/a.txt", new string('b', 20_000) + "the second content");
            await BackUpAsync(runtime);
        }

        CopyDirectory(_harness.StateDirectory, OlderState);
        var replica = await ReplicaPathAsync();
        var dataBlobsBefore = DataBlobs(replica);

        await using (var runtime = await StartAsync())
        {
            _harness.WriteSourceFile("docs/a.txt", new string('a', 20_000) + "the first content");
            await BackUpAsync(runtime);
        }

        Assert.AreEqual(
            dataBlobsBefore, DataBlobs(replica),
            "the third backup must reuse the first's segment, or the scenario proves nothing");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        CopyDirectory(OlderState, _harness.StateDirectory);
        return replica;
    }

    private static int DataBlobs(string replica) =>
        Directory.Exists(Path.Combine(replica, "blobs", "data"))
            ? Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories).Length
            : 0;

    [TestMethod]
    public async Task TheCurrentStateDirectory_RaisesNothingAtThePeer()
    {
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, directShip: true);
        _harness.WriteSourceFile("docs/a.txt", "day one content");

        await using var runtime = await StartAsync();
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/a.txt", "second content");
        await BackUpAsync(runtime);
        await SyncAsync(runtime);

        Assert.IsEmpty(runtime.Notices.Unacknowledged.Where(n => n.Key.StartsWith("destination-ahead:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AStagingSet_RolledBackWithItsArchive_IsHealedOverTheRetrievalSession()
    {
        // A staging set's peer keeps what staging no longer lists, and the
        // retrieval session is exactly how this machine reads it back: the
        // pass notices from the inventory, moves the writer, and copies the
        // newer history — content and metadata, blobs first, one chunk in
        // memory at a time — into the staging archive. The second backup
        // carries a file longer than one retrieval chunk, so the copy has
        // to reassemble it, and the healed archive is held byte for byte
        // against the peer's copy.
        var replica = await TwoBackupsThenRollBackAsync(directShip: false, bigSecondBackup: true);
        // A scheduled sync holds no authority to delete at a peer (ADR-0055
        // §6), so the peer holds both backups; the rolled-back archive lists
        // the first alone, and the second is what it is owed.
        var before = ReplicaKeys(replica);
        Assert.HasCount(2, SnapshotObjects(replica));
        Assert.ContainsSingle(SnapshotObjects(Staging), "the rolled-back archive holds the first backup alone");
        var newest = Assert.ContainsSingle(SnapshotObjects(replica).Except(SnapshotObjects(Staging), StringComparer.Ordinal));
        var sequenceBefore = await SequenceFileAsync();

        await using var runtime = await StartAsync();
        await SyncAsync(runtime);

        var notice = Assert.ContainsSingle(runtime.Notices.Unacknowledged.Where(n => n.Key == NoticeKey));
        Assert.Contains("staging", notice.Message, StringComparison.Ordinal);
        Assert.Contains("copied back", notice.Message, StringComparison.Ordinal);
        Assert.AreNotEqual(sequenceBefore, await SequenceFileAsync());
        Assert.IsEmpty(before.Except(ReplicaKeys(replica), StringComparer.Ordinal));

        var spansChunks = false;
        foreach (var key in before)
        {
            var theirs = await File.ReadAllBytesAsync(Path.Combine(replica, key), Timeout);
            var mine = Path.Combine(Staging, key);
            Assert.IsTrue(File.Exists(mine), $"{key} was not copied back into the staging archive");
            var ours = await File.ReadAllBytesAsync(mine, Timeout);
            Assert.IsTrue(theirs.AsSpan().SequenceEqual(ours), $"{key} came back from the peer as different bytes");
            spansChunks |= theirs.Length > RetrieveRead.MaximumLength;
        }

        Assert.IsTrue(spansChunks, "no object at the peer was longer than one retrieval chunk, so the reassembly went unexercised");
        Assert.HasCount(2, SnapshotObjects(Staging));

        // The granted run converges from an archive that now knows the
        // second backup: under KeepDaily=1 the peer keeps exactly the newest.
        await ApplyRetentionAsync(runtime);
        Assert.AreEqual(newest, Assert.ContainsSingle(SnapshotObjects(replica)), "the granted run trimmed the newer backup");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.AreEqual("second content", await RestoreNewestAsync(handler, "restored-after-peer-heal", "a.txt"));
        _harness.WriteSourceFile("docs/a.txt", "third content");
        await BackUpAsync(runtime);
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var relisted);
        Assert.HasCount(3, relisted.Snapshots);
    }

    private string Staging => Path.Combine(_harness.ArchivesRoot, _harness.DocsSetId);

    private static List<string> SnapshotObjects(string root) =>
        [.. Directory.GetFiles(Path.Combine(root, "snapshots"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))];

    /// <summary>Restores one file from the newest snapshot of the set's own archive, under a grant.</summary>
    private async Task<string> RestoreNewestAsync(ServiceCommandHandler handler, string into, string fileName)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        var newest = listed.Snapshots.OrderByDescending(snapshot => snapshot.CapturedAt).First().SnapshotId;
        var output = Path.Combine(_harness.WorkPath, into);
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest, null, output,
                    Source: (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout)).SourceId),
                Timeout),
            out var restored);
        Assert.AreEqual("complete", restored.Outcome);
        var file = Assert.ContainsSingle(Directory.GetFiles(output, fileName, SearchOption.AllDirectories));
        return await File.ReadAllTextAsync(file, Timeout);
    }

    /// <summary>
    /// Two backups to the peer, the state directory (and, for a staging set,
    /// the archives root) copied aside between them and put back afterwards.
    /// </summary>
    private async Task<string> TwoBackupsThenRollBackAsync(bool directShip, bool bigSecondBackup = false)
    {
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, directShip);
        _harness.WriteSourceFile("docs/a.txt", "day one content");

        // A staging set reaches its peer only through the fan-out pass; a
        // direct-ship set ships during the run and the pass is a no-op.
        await using (var runtime = await StartAsync())
        {
            await BackUpAsync(runtime);
            await SyncAsync(runtime);
        }

        CopyDirectory(_harness.StateDirectory, OlderState);
        if (!directShip)
        {
            CopyDirectory(_harness.ArchivesRoot, OlderArchives);
        }

        await using (var runtime = await StartAsync())
        {
            _harness.WriteSourceFile("docs/a.txt", "second content");
            if (bigSecondBackup)
            {
                // Incompressible and longer than one retrieval chunk, so the
                // blob that seals it is too.
                var big = new byte[(2 * RetrieveRead.MaximumLength) + 4_097];
                Random.Shared.NextBytes(big);
                await File.WriteAllBytesAsync(Path.Combine(_harness.SourceRoot, "docs", "big.bin"), big, Timeout);
            }

            await BackUpAsync(runtime);
            await SyncAsync(runtime);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_harness.StateDirectory, recursive: true);
        CopyDirectory(OlderState, _harness.StateDirectory);
        if (!directShip)
        {
            Directory.Delete(_harness.ArchivesRoot, recursive: true);
            CopyDirectory(OlderArchives, _harness.ArchivesRoot);
        }

        return await ReplicaPathAsync();
    }

    // The real clock for every operation: the granted convergence run
    // stamps the ledger with it, and a backup stamped ahead of it would read
    // to the sink as a destination that missed a run.
    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var queued = FanOut.EnqueueAll(
            runtime, runtime.Configuration.BackupSets.Single(), DateTimeOffset.Now, userInitiated: true);
        await Task.WhenAll(queued).WaitAsync(Timeout);
    }

    /// <summary>The granted collection run (ADR-0055 §6), which is what converges a peer.</summary>
    private async Task ApplyRetentionAsync(ServiceRuntime runtime)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _harness.ReclaimGrantAsync(handler, Timeout);
        Assert.IsInstanceOfType<RetentionResult>(
            await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout));
    }

    private async Task<string> SequenceFileAsync()
    {
        var path = Assert.ContainsSingle(Directory.GetFiles(_harness.StateDirectory, "sequence-*.txt"));
        return await File.ReadAllTextAsync(path, Timeout);
    }

    /// <summary>Every journal record this machine holds is at the peer, byte for byte.</summary>
    private async Task AssertJournalsAgreeAsync(string replicaPath)
    {
        var local = new LocalFileSystemObjectStore(MetadataRoot);
        var replica = new LocalFileSystemObjectStore(replicaPath);
        var seen = 0;
        await foreach (var entry in local.ListAsync(ObjectPrefix.Parse("journal/"), ListOptions.Default, Timeout))
        {
            seen++;
            var mine = await ReadAsync(local, entry.Key);
            var theirs = await ReadAsync(replica, entry.Key);
            Assert.IsNotNull(theirs, $"the peer is missing {entry.Key.Value}");
            Assert.IsTrue(
                mine.AsSpan().SequenceEqual(theirs),
                $"the peer's {entry.Key.Value} is not the record this machine published under that sequence");
        }

        Assert.IsGreaterThan(0, seen);
    }

    private async Task<byte[]?> ReadAsync(LocalFileSystemObjectStore store, ObjectKey key)
    {
        using var read = await store.OpenReadAsync(key, range: null, Timeout);
        if (read.Outcome != OpenReadOutcome.Found || read.Content is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await read.Content.CopyToAsync(buffer, Timeout);
        return buffer.ToArray();
    }

    private static List<string> ReplicaKeys(string replica) =>
        [.. Directory.GetFiles(replica, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(replica, path).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith(".fbp-tmp", StringComparison.Ordinal))];

    private static bool IsMetadataKey(string key) =>
        !key.StartsWith("blobs/", StringComparison.Ordinal)
        && !key.StartsWith("tombstones/", StringComparison.Ordinal)
        && !key.StartsWith("leases/", StringComparison.Ordinal);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1
                && File.Exists(Path.Combine(directories[0], "repository-format"))
                && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the peer never took a whole replica");
            await Task.Delay(100, Timeout);
        }
    }

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private void WriteConfiguration(string fingerprint, bool directShip, bool withVault = false)
    {
        List<DestinationConfiguration> destinations =
        [
            new()
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ];
        List<SetDestinationReference> references =
        [
            new() { Ref = "friend", Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 } },
        ];
        if (withVault)
        {
            destinations.Add(new()
            {
                Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
            });
            references.Add(new() { Ref = "vault" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 4h",
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    private async Task<string> StartDestinationAsync()
    {
        await _harness.SetupAsync();
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _timeout.Dispose();
        _harness.Dispose();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
