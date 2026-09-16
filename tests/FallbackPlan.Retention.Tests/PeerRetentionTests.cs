using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Retention against a peer (peer-protocol 06, FR-GC-010): the hub computes,
/// the spoke deletes exactly what it is told, and the granted floor is the
/// one safeguard that holds when the hub is compromised — an instruction
/// below it is refused whole, deleting nothing.
/// Establishes FR-GC-007, and FR-GC-008's peer half (ADR-0055 §5): the spoke
/// records the reclaim public key the offer publishes and acts on an
/// instruction signed under it, and refuses whole — deleting nothing, exactly
/// as a floor breach is refused — when the signature does not verify against
/// the key it holds.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PeerRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-peer-retention", Guid.NewGuid().ToString("n"));

    private const string PassphraseText = "peer-retention-passphrase!!";
    private const ulong PairedAt = 1_722_600_000_000;
    private static readonly string SetId = new('a', 32);

    private string ArchivesRoot => Path.Combine(_root, "archives");
    private string StateDirectory => Path.Combine(_root, "state");
    private string SourceRoot => Path.Combine(_root, "source");
    private string DestinationState => Path.Combine(_root, "destination");

    private IPEndPoint? _endpoint;
    private Stopper? _stop;
    private PeerGrantStore? _destinationGrants;

    public PeerRetentionTests()
    {
        Directory.CreateDirectory(StateDirectory);
        WriteOnlyInstallation.Provision(StateDirectory, PassphraseText);
        Directory.CreateDirectory(SourceRoot);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "peer retention fodder");
    }

    [TestMethod]
    public async Task FanOut_APeerUnderANarrowPolicy_ConvergesTheReplicaToItsKeepSet()
    {
        var fingerprint = StartDestination(floorGenerations: 0);
        WriteConfiguration(fingerprint);

        // Three backups, each pass pushing and then instructing: the spoke
        // ends holding exactly the keep-set — one snapshot — not the three
        // staging holds.
        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(start);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "third content");
        await BackUpAsync(start.AddHours(10));

        // A scheduled pass holds no authority to delete (ADR-0055 §6): the
        // spoke received whole copies, the pass says why, and nothing was
        // refused — an unsigned instruction was never sent.
        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());
        Assert.HasCount(3, await ListAsync(replica, "snapshots/"));
        Assert.AreEqual(
            DestinationSyncState.InSync, DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend")!.State);
        Assert.Contains(
            notice => notice.Message.Contains("reclaim grant", StringComparison.OrdinalIgnoreCase),
            NoticeStore.Open(StateDirectory).Unacknowledged);

        // The granted run is what instructs: the spoke ends holding exactly
        // the keep-set — one snapshot — and the notice is resolved.
        await ApplyRetentionAsync();
        Assert.HasCount(1, await ListAsync(replica, "snapshots/"));
        Assert.DoesNotContain(
            notice => notice.Message.Contains("reclaim grant", StringComparison.OrdinalIgnoreCase),
            NoticeStore.Open(StateDirectory).Unacknowledged);

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, record!.State);

        // The replica the spoke holds is a valid archive of exactly that
        // keep-set: it opens with the passphrase and walks clean.
        using var archive = await WriteOnlyInstallation.OpenAsync(replica, PassphraseText, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(replica, archive.Repository, CancellationToken.None);
        Assert.ContainsSingle(survey.Snapshots);
        Assert.IsEmpty(survey.Undecodable);
    }

    [TestMethod]
    public async Task FanOut_AnInstructionBelowTheFloor_IsRefusedWholeAndDeletesNothing()
    {
        // The spoke granted a floor of three generations. The hub's policy
        // keeps one — the instruction would breach the floor, so the spoke
        // refuses it entirely, and the pushed history stays.
        var fingerprint = StartDestination(floorGenerations: 3);
        WriteConfiguration(fingerprint);

        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(start);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "third content");
        await BackUpAsync(start.AddHours(10));

        await ApplyRetentionAsync();
        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());

        // Everything pushed survives: the refusal is loud and total, never a
        // partial best-effort delete (06 §3).
        Assert.HasCount(3, await ListAsync(replica, "snapshots/"));

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend");
        Assert.AreEqual(DestinationSyncState.Failed, record!.State);
        Assert.Contains("floor", record.LastError!, StringComparison.OrdinalIgnoreCase);

        // The refusal is visible at the hub as a durable notice (FR-GC-010).
        Assert.Contains(
            notice => notice.Message.Contains("floor", StringComparison.OrdinalIgnoreCase),
            NoticeStore.Open(StateDirectory).Unacknowledged);
    }

    [TestMethod]
    public async Task FanOut_TheSpokeRecordsTheReclaimKeyAndActsOnTheSignedInstruction()
    {
        // The peer half of ADR-0055 end to end. The spoke has no repository
        // keys, so the only thing it can check a deletion instruction against
        // is the reclaim public key the source published when the repository
        // was first attributed to it.
        var fingerprint = StartDestination(floorGenerations: 0);
        WriteConfiguration(fingerprint);

        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(start);
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        await ApplyRetentionAsync();

        var owner = ReplicaOwnerStore.Open(DestinationState).Find(RepositoryIdHex());
        Assert.IsNotNull(owner);
        Assert.IsNotNull(owner.ReclaimPublicKey, "the offer must publish the key the spoke will check against");
        Assert.HasCount(64, owner.ReclaimPublicKey!);

        // And the signed instruction was acted on, so signing did not merely
        // fail to break anything — it went through the whole exchange.
        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());
        Assert.HasCount(1, await ListAsync(replica, "snapshots/"));
        Assert.AreEqual(
            DestinationSyncState.InSync,
            DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend")!.State);
    }

    [TestMethod]
    public async Task FanOut_TheSpokesRecordedKeyIsNotTheRepositorys_TheInstructionIsRefusedWhole()
    {
        // The attack the signature closes: an instruction that did not come
        // from the reclaim authority. Swapping the spoke's recorded key is the
        // cheapest way to make a genuine instruction fail to verify, and what
        // it proves is the check runs and is total — nothing is deleted.
        var fingerprint = StartDestination(floorGenerations: 0);
        WriteConfiguration(fingerprint);

        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(start);

        // The spoke holds its attribution store in memory, so the swap has to
        // happen while it is down — which is also the honest shape of the
        // attack: somebody with the destination's disk, not its process.
        await _stop!.DisposeAsync();
        _stop = null;

        var ownersPath = Path.Combine(DestinationState, "replica-owners.json");
        var recorded = await File.ReadAllTextAsync(ownersPath);
        var real = ReplicaOwnerStore.Open(DestinationState).Find(RepositoryIdHex())!.ReclaimPublicKey!;
        await File.WriteAllTextAsync(ownersPath, recorded.Replace(real, new string('a', 64), StringComparison.Ordinal));

        RestartDestination();
        WriteConfiguration(fingerprint);

        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        await ApplyRetentionAsync();

        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());

        // Both snapshots survive: a refusal is whole, exactly as the floor
        // breach is, because a partially-honoured instruction whose authorship
        // is in doubt is the worst of both answers.
        Assert.HasCount(2, await ListAsync(replica, "snapshots/"));

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend");
        Assert.AreEqual(DestinationSyncState.Failed, record!.State);
        Assert.Contains("reclaim", record.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    private void RestartDestination()
    {
        var listenerKeypair = PeerKeypairStore.Open(DestinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, PeerGrantStore.Open(DestinationState), new IPEndPoint(IPAddress.Loopback, 0),
            "fallbackplan-agent/test", log: null, replicationStateDirectory: DestinationState);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);
    }

    private string RepositoryIdHex() =>
        Path.GetFileName(Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());

    [TestMethod]
    public async Task FanOut_AKeyOnlyTheSpokeHolds_SurvivesTheRetentionInstruction()
    {
        // After a staging trim, a data blob's only remaining copy may sit at
        // the spoke. The drop-list is inventory minus keep-set, and the
        // keep-set is computed from staging — which has never heard of the
        // trimmed key. Only keys staging still lists may be condemned
        // (ADR-0034 §6), so the planted key must ride out the instruction.
        var fingerprint = StartDestination(floorGenerations: 0);
        WriteConfiguration(fingerprint);

        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await BackUpAsync(start);

        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());
        var planted = ObjectKey.Parse("blobs/data/zz/trimmed-from-staging");
        var put = await replica.PutAsync(
            planted,
            _ => ValueTask.FromResult<Stream>(new MemoryStream("planted"u8.ToArray())),
            PutConditions.None,
            CancellationToken.None);
        Assert.AreEqual(PutOutcome.Created, put.Outcome);

        // Two more passes pushing, then the granted run instructing under
        // the narrow policy; the spoke declares the planted key in every
        // inventory.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "third content");
        await BackUpAsync(start.AddHours(10));
        await ApplyRetentionAsync();

        Assert.HasCount(1, await ListAsync(replica, "snapshots/"));
        var metadata = await replica.GetMetadataAsync(planted, CancellationToken.None);
        Assert.IsNotNull(metadata.Metadata, "the key only the spoke holds was condemned by a staging-computed drop-list");
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
    }.Save(Path.Combine(StateDirectory, "config.json"));

    private string StartDestination(uint floorGenerations)
    {
        using var sourceKeypair = PeerKeypairStore.Open(StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(DestinationState);

        _destinationGrants = PeerGrantStore.Open(DestinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere,
            new PeerTerms(0, string.Empty, floorGenerations), PairedAt));

        PeerGrantStore.Open(StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(DestinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: DestinationState);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);

        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>
    /// The granted collection run (ADR-0055 §6): the same command a console
    /// sends, with the reclaim grant the passphrase derives — which is what
    /// instructs the spoke, since a scheduled pass holds no authority to
    /// delete.
    /// </summary>
    private async Task ApplyRetentionAsync()
    {
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = ArchivesRoot, StateDirectory = StateDirectory },
            passphrase: null, CancellationToken.None);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var description = (Api.ServiceDescriptionResult)await handler.ExecuteAsync(
            new Api.DescribeServiceCommand(), CancellationToken.None);
        var applied = await handler.ExecuteAsync(
            new Api.RetentionCommand(
                Apply: true,
                ReclaimGrant: WriteOnlyInstallation.ReclaimGrant(
                    StateDirectory, PassphraseText, description.RestoreGrantRecipient!)),
            CancellationToken.None);
        Assert.IsInstanceOfType<Api.RetentionResult>(
            applied, (applied as Api.ServiceError)?.Message ?? applied.GetType().Name);
    }

    private async Task BackUpAsync(DateTimeOffset now)
    {
        using var passphrase = Passphrase.Create(PassphraseText);
        var result = await AgentPass.RunAsync(ArchivesRoot, passphrase, StateDirectory, now, CancellationToken.None);
        Assert.AreEqual(1, result.Ran, string.Join("; ", result.Sets.Select(set => $"{set.Outcome}:{set.Detail}")));
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
    private sealed class UnusedService : Api.IFallbackPlanService
    {
        public ValueTask<Api.ServiceResult> ExecuteAsync(Api.ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<Api.JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
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
