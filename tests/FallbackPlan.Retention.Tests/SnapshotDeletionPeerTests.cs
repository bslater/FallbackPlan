using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A deletion a person asked for reaches a paired peer (FR-GC-013,
/// ADR-0080): a peer with no retention rules, which otherwise keeps every
/// snapshot it is sent, is instructed under the set's reclaim grant to drop
/// the requested snapshot, and answers with a deletion receipt (ADR-0063) as
/// it would for retention. A scheduled sync holds no authority to delete
/// (ADR-0055 §6), so while the request is pending it neither instructs the
/// drop nor sends the snapshot to a peer that lacks it.
/// </summary>
[TestClass]
public sealed class SnapshotDeletionPeerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-deletion-peer", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "deletion-peer-passphrase!!";
    private const ulong PairedAt = 1_722_600_000_000;
    private static readonly string SetId = new('a', 32);
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string RepoPath => Path.Combine(ArchivesRoot, SetId);
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string DestinationState => Path.Combine(_root, "destination");

    private IPEndPoint? _endpoint;
    private Stopper? _stop;

    public SnapshotDeletionPeerTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day one content");
    }

    [TestMethod]
    public async Task Apply_APeerWithNoRules_DropsTheSnapshotUnderTheGrant_AndAnswersWithAReceipt()
    {
        WriteConfiguration(StartDestination());
        var snapshots = await BackUpThreeAsync();
        Assert.HasCount(3, await ListAsync(Replica, "snapshots/"));
        Assert.IsEmpty(DeletionReceiptStore.Open(StateDirectory).List());

        var result = await DeleteAsync(snapshots[1].Fact.SnapshotId);

        Assert.AreEqual("deleted", Assert.ContainsSingle(result.Snapshots).State);
        var held = await ListAsync(Replica, "snapshots/");
        Assert.HasCount(2, held);
        Assert.DoesNotContain(snapshots[1].StoreKey.Value, held);
        Assert.HasCount(2, await ListAsync(new LocalFileSystemObjectStore(RepoPath), "snapshots/"));

        // The peer attested what it did, as it does for retention.
        var filed = Assert.ContainsSingle(DeletionReceiptStore.Open(StateDirectory).List());
        CollectionAssert.Contains(filed.Receipt!.Deleted.ToList(), snapshots[1].StoreKey.Value);
        Assert.Contains(
            line => line.Contains(Path.GetFileName(filed.Path), StringComparison.Ordinal), result.Lines);
    }

    [TestMethod]
    public async Task ScheduledSync_WhileADeletionIsPending_NeitherDropsNorSendsTheSnapshot()
    {
        WriteConfiguration(StartDestination());
        var snapshots = await BackUpThreeAsync();
        var target = snapshots[1];
        var request = await RequestAsync(target);

        // No grant, no drop: the peer keeps what it holds, and the pair is not
        // counted as converged since the request.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day four content");
        await BackUpAsync(Start.AddDays(3));
        Assert.Contains(target.StoreKey.Value, await ListAsync(Replica, "snapshots/"));
        Assert.IsLessThan(request.Generation, DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend")!.ConvergedSequence);

        // A peer that lacks the snapshot is not sent it while the request
        // stands, though a scheduled sync would otherwise send it whole.
        var removed = await Replica.DeleteAsync(target.StoreKey, DeleteConditions.None, CancellationToken.None);
        Assert.AreEqual(DeleteOutcome.Deleted, removed.Outcome);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day five content");
        await BackUpAsync(Start.AddDays(4));
        Assert.DoesNotContain(target.StoreKey.Value, await ListAsync(Replica, "snapshots/"));
    }

    private LocalFileSystemObjectStore Replica =>
        new(Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());

    private async Task<SnapshotDeletionRequest> RequestAsync(SurveyedSnapshot target)
    {
        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(
            StateDirectory, $"sequence-{Convert.ToHexStringLower(opened.Repository.RepositoryId.ToArray())}.txt")));

        return await SnapshotDeletion.RequestAsync(
            store, opened.Repository, WriterId.FromBytes(LocalState.LoadOrCreate(StateDirectory).WriterId),
            sequence, [target], "cli",
            (ulong)Start.AddDays(2).AddHours(1).ToUnixTimeMilliseconds(), CancellationToken.None, opened.Reclaim);
    }

    private async Task<DeleteSnapshotsResult> DeleteAsync(string snapshotId)
    {
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = ArchivesRoot, StateDirectory = StateDirectory },
            CancellationToken.None);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var description = (ServiceDescriptionResult)await handler.ExecuteAsync(
            new DescribeServiceCommand(), CancellationToken.None);

        var result = await handler.ExecuteAsync(
            new DeleteSnapshotsCommand(
                SetId, [snapshotId], Apply: true,
                ReclaimGrant: WriteOnlyInstallation.ReclaimGrant(
                    StateDirectory, PassphraseText, description.RestoreGrantRecipient!)),
            CancellationToken.None);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            result, (result as ServiceError)?.Message ?? result.GetType().Name);
        return (DeleteSnapshotsResult)result;
    }

    /// <summary>Three daily backups, each pushed whole to the peer; the snapshots, oldest first.</summary>
    private async Task<IReadOnlyList<SurveyedSnapshot>> BackUpThreeAsync()
    {
        await BackUpAsync(Start);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day two content");
        await BackUpAsync(Start.AddDays(1));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "day three content");
        await BackUpAsync(Start.AddDays(2));

        var store = new LocalFileSystemObjectStore(RepoPath);
        using var opened = await WriteOnlyInstallation.OpenAsync(store, PassphraseText, CancellationToken.None);
        return [.. (await StagingMark.SurveyAsync(store, opened.Repository, CancellationToken.None)).Snapshots.Reverse()];
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        var result = await AgentPass.RunAsync(ArchivesRoot, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
    }

    private void WriteConfiguration(string fingerprint) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = fingerprint,
                Endpoint = $"{_endpoint!.Address}:{_endpoint.Port}",
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = SetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                Schedule = "every 4h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private string StartDestination()
    {
        using var sourceKeypair = PeerKeypairStore.Open(StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(DestinationState);

        var destinationGrants = PeerGrantStore.Open(DestinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, new PeerTerms(0, string.Empty, 0), PairedAt));

        PeerGrantStore.Open(StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(DestinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: DestinationState);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);

        return destinationKeypair.Identity.Fingerprint;
    }

    private static async Task<List<string>> ListAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    private sealed class Stopper(RemoteServiceListener listener, PeerKeypair keypair) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await listener.DisposeAsync();
            keypair.Dispose();
        }
    }

    public void Dispose()
    {
        _stop?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
