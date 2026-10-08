using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Restore;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer that dies in front of each delete of a retention instruction is
/// left holding a replica that restores every snapshot it lists, and the next
/// granted run finishes the instruction (FR-GC-006 at a peer; FR-GC-010;
/// peer-protocol 06).
/// </summary>
/// <remarks>
/// <para>
/// The spoke reads every page of an instruction before it deletes anything,
/// and then deletes in the order it was sent. The hub ranks snapshots first,
/// so a spoke that dies part-way has dropped whole snapshots and some of what
/// only they reached, never a blob a snapshot it still lists needs.
/// </para>
/// <para>
/// The spoke is killed through
/// <see cref="ReplicationResponder.RetentionStoreDecorator"/>, set before its
/// listener starts so that every session the listener serves carries it. A
/// death ends that session, as a spoke's process ending would, and the
/// listener serves the next run as a restarted spoke would.
/// </para>
/// </remarks>
[TestClass]
public sealed class PeerRetentionInterruptionTests : IDisposable
{
    private readonly HostHarness _source = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-retention-cut", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private Protocol.PeerIdentity? _destinationIdentity;

    [TestMethod]
    public async Task ARetentionInstruction_TheSpokeDyingInFrontOfEachDelete_LeavesEverySnapshotItListsRestorable_AndTheNextRunFinishesIt()
    {
        var dying = new DyingDeletes();
        ReplicationResponder.RetentionStoreDecorator = dying.Wrap;
        StartDestination();
        await _source.SetupAsync();
        WriteConfiguration();

        // A scheduled pass holds no grant, so the spoke takes whole copies
        // of all three days and drops nothing (ADR-0055 §6).
        var days = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var day = 0; day < 3; day++)
        {
            days[await BackUpDayAsync(day)] = day;
        }

        var replicaPath = Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single();
        Assert.HasCount(3, StoredKeys(replicaPath).Where(key => key.StartsWith("snapshots/", StringComparison.Ordinal)));
        var pristine = Path.Combine(_destinationState, "pristine");
        CopyDirectory(replicaPath, pristine);

        // The granted run instructs the spoke down to its keep-set: the
        // newest snapshot. The uncut instruction is what every cut must come
        // back to.
        await ApplyRetentionAsync();
        var asked = dying.Asked;
        var converged = StoredKeys(replicaPath);
        Assert.ContainsSingle(converged.Where(key => key.StartsWith("snapshots/", StringComparison.Ordinal)));
        Assert.IsTrue(asked.Any(key => key.StartsWith("snapshots/", StringComparison.Ordinal)), string.Join(" | ", asked));
        Assert.IsTrue(asked.Any(key => key.StartsWith("blobs/", StringComparison.Ordinal)), string.Join(" | ", asked));

        for (var cut = 1; cut <= asked.Count; cut++)
        {
            var at = $"the spoke dying in front of delete {cut} of {asked.Count}, {asked[cut - 1]}";
            Directory.Delete(replicaPath, recursive: true);
            CopyDirectory(pristine, replicaPath);

            dying.Arm(cut);
            await ApplyRetentionAsync();
            Assert.IsTrue(dying.Died, $"{at}: the instruction never reached the cut");
            await AssertEveryListedSnapshotRestoresAsync(replicaPath, days, at);

            dying.Arm(int.MaxValue);
            await ApplyRetentionAsync();
            Assert.IsTrue(
                StoredKeys(replicaPath).SequenceEqual(converged),
                $"{at}: the next run left another replica. Only after the cut: "
                + string.Join(", ", StoredKeys(replicaPath).Except(converged))
                + ". Missing: " + string.Join(", ", converged.Except(StoredKeys(replicaPath))));
        }
    }

    /// <summary>
    /// Counts the deletes the spoke makes under instruction, across every
    /// session, and kills it in front of the armed one.
    /// </summary>
    private sealed class DyingDeletes
    {
        private readonly List<string> _asked = [];
        private int _cut = int.MaxValue;

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _asked];
                }
            }
        }

        public bool Died { get; private set; }

        public void Arm(int cut)
        {
            lock (_asked)
            {
                _asked.Clear();
                _cut = cut;
                Died = false;
            }
        }

        public IObjectStore Wrap(IObjectStore replica) => new Dying(replica, this);

        private void Delete(ObjectKey key)
        {
            lock (_asked)
            {
                _asked.Add(key.Value);
                if (_asked.Count >= _cut)
                {
                    Died = true;
                    throw new IOException($"the spoke died in front of deleting {key.Value}");
                }
            }
        }

        private sealed class Dying(IObjectStore inner, DyingDeletes owner) : IObjectStore
        {
            public StoreCapabilities Capabilities => inner.Capabilities;

            public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
                inner.GetMetadataAsync(key, cancellationToken);

            public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
                inner.OpenReadAsync(key, range, cancellationToken);

            public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
                inner.ListAsync(prefix, options, cancellationToken);

            public ValueTask<PutResult> PutAsync(
                ObjectKey key,
                Func<CancellationToken, ValueTask<Stream>> openContent,
                PutConditions conditions,
                CancellationToken cancellationToken) =>
                inner.PutAsync(key, openContent, conditions, cancellationToken);

            public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
            {
                owner.Delete(key);
                return inner.DeleteAsync(key, conditions, cancellationToken);
            }
        }
    }

    /// <summary>The granted collection run a console sends, which is what instructs a peer (ADR-0055 §6).</summary>
    private async Task ApplyRetentionAsync()
    {
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _source.ArchivesRoot, StateDirectory = _source.StateDirectory },
            Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var grant = await _source.ReclaimGrantAsync(handler, Timeout);
        var applied = await handler.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout);
        Assert.IsInstanceOfType<RetentionResult>(applied, (applied as ServiceError)?.Message ?? applied.GetType().Name);
    }

    /// <summary>A day's backup, pushed to the spoke, returning the snapshot it published.</summary>
    private async Task<string> BackUpDayAsync(int day)
    {
        foreach (var (name, bytes) in Files(day))
        {
            File.WriteAllBytes(Path.Combine(_source.SourceRoot, name), bytes);
        }

        var before = Directory.Exists(_source.RepositoryPath) ? await ListedSnapshotsAsync(_source.RepositoryPath) : [];
        var pass = await AgentPass.RunAsync(
            _source.ArchivesRoot, _source.StateDirectory,
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero).AddDays(day), Timeout);
        Assert.AreEqual(1, pass.Ran, string.Join("; ", pass.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
        return (await ListedSnapshotsAsync(_source.RepositoryPath)).Except(before).Single();
    }

    private async Task<List<string>> ListedSnapshotsAsync(string storeRoot)
    {
        var store = new LocalFileSystemObjectStore(storeRoot);
        var (repository, authority) = await OpenAsync(store);
        using (repository)
        using (authority)
        {
            var survey = await StagingMark.SurveyAsync(store, repository, Timeout);
            return [.. survey.Snapshots.Select(snapshot => snapshot.Fact.SnapshotId)];
        }
    }

    /// <summary>
    /// Every snapshot the replica lists, restored from the replica alone
    /// against the files its day backed up. The set's catalogue plans the
    /// restore, and the replica's own index finds the bytes.
    /// </summary>
    private async Task AssertEveryListedSnapshotRestoresAsync(
        string replicaPath, Dictionary<string, int> days, string at)
    {
        var store = new LocalFileSystemObjectStore(replicaPath);
        var (repository, authority) = await OpenAsync(store);
        using (repository)
        using (authority)
        {
            using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
            await reader.LoadBlobsAsync(Timeout);
            using var catalogue = CatalogueDb.Open(
                Path.Combine(_source.StateDirectory, $"catalogue-{repository.RepositoryId}.db"), repository.RepositoryId);
            var target = RestoreTargetProfile.ForLocalPlatform();

            var survey = await StagingMark.SurveyAsync(store, repository, Timeout);
            Assert.IsNotEmpty(survey.Snapshots, $"{at}: the replica lists no snapshot");
            foreach (var snapshot in survey.Snapshots)
            {
                var day = days[snapshot.Fact.SnapshotId];
                var output = Path.Combine(_destinationState, "restored", Guid.NewGuid().ToString("n"));
                var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
                    RestorePlanner.Plan(catalogue, Convert.FromHexString(snapshot.Fact.SnapshotId), string.Empty, target),
                    output,
                    new RestoreExecutionOptions
                    {
                        DestinationMode = RestoreDestinationMode.InPlace,
                        RunId = $"cut-{day}",
                        NowUnixMilliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    Timeout);

                Assert.AreEqual(
                    RestoreOutcome.Complete, receipt.Outcome, $"{at}: day {day}'s snapshot, still listed, did not restore");
                foreach (var (name, bytes) in Files(day))
                {
                    Assert.IsTrue(
                        bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, name))),
                        $"{at}: day {day}'s {name} came back different");
                }
            }
        }
    }

    private async Task<(OpenedRepository Repository, RepositoryReadAuthority Authority)> OpenAsync(IObjectStore store)
    {
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        return await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
    }

    private static Dictionary<string, byte[]> Files(int day)
    {
        var bulk = new byte[200 * 1024];
        new Random(day + 1).NextBytes(bulk);
        return new Dictionary<string, byte[]>
        {
            ["a.txt"] = System.Text.Encoding.UTF8.GetBytes($"day {day} content"),
            ["bulk.bin"] = bulk,
        };
    }

    private static List<string> StoredKeys(string path) =>
        [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }

    private void WriteConfiguration() => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = _destinationIdentity!.Fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = _source.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _source.SourceRoot }],
                Schedule = "every 4h",
                Destinations =
                [
                    new SetDestinationReference
                    {
                        Ref = "friend",
                        Retention = new RetentionConfiguration { KeepDaily = 1, MinGenerations = 1 },
                    },
                ],
            },
        ],
    }.Save(Path.Combine(_source.StateDirectory, "config.json"));

    private void StartDestination()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        // No floor, so the floor is never what holds a delete back (FR-GC-007).
        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        PeerGrantStore.Open(_source.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;
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
        _source.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_destinationState))
            {
                Directory.Delete(_destinationState, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
