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
/// Establishes FR-GC-007.
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

        var replica = new LocalFileSystemObjectStore(
            Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Single());
        Assert.HasCount(1, await ListAsync(replica, "snapshots/"));

        var record = DestinationSyncStore.Open(StateDirectory).Find(SetId, "friend");
        Assert.AreEqual(DestinationSyncState.InSync, record!.State);

        // The replica the spoke holds is a valid archive of exactly that
        // keep-set: it opens with the passphrase and walks clean.
        using var passphrase = Passphrase.Create(PassphraseText);
        using var opened = await FallbackPlan.Repository.RepositoryLifecycle.OpenAsync(
            replica, passphrase, CancellationToken.None);
        var survey = await StagingMark.SurveyAsync(replica, opened, CancellationToken.None);
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

        // Two more passes, each pushing and instructing under the narrow
        // policy; the spoke declares the planted key in every inventory.
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "second content");
        await BackUpAsync(start.AddHours(5));
        File.WriteAllText(Path.Combine(SourceRoot, "a.txt"), "third content");
        await BackUpAsync(start.AddHours(10));

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
