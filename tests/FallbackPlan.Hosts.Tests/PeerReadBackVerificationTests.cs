using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer replica is proved by reading it back, where there is nothing to
/// compare it against (FR-VER-001, FR-VER-002,
/// [ADR-0058](../../docs/adr/0058-peer-write-adapter.md) §8).
/// </summary>
/// <remarks>
/// <para>
/// The wire challenge judges a peer's answer against bytes this side reads for
/// itself, so a direct-ship set whose only destination is that peer cannot use
/// it: the content lives nowhere else. ADR-0058 took the honest way out and
/// stamped nothing, which is right and is not the end of it — such a set then
/// has no proof of its content at all, for ever.
/// </para>
/// <para>
/// The proof that needs no second copy already exists for local paths: open a
/// sampled blob at the replica and authenticate a record inside it under the
/// repository's own key, which the destination has never held. Reaching it
/// through the retrieval session is what this suite is about.
/// </para>
/// <para>
/// The samples come from the peer's own replication inventory, and that is a
/// closed loop rather than letting the peer choose what is examined: a key it
/// omits to avoid being asked about is a key the same session re-ships, so
/// hiding a loss repairs it.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PeerReadBackVerificationTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-readback", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private Protocol.PeerIdentity? _destinationIdentity;

    [TestMethod]
    public async Task Pass_APeerHoldingTheOnlyCopy_ProvesItByReadingItBack()
    {
        await SeedAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");
        Assert.IsNotNull(
            record.VerifiedAt,
            "a set whose only destination is a peer went unproven — the replica can be opened over the "
                + "retrieval session and its records authenticated without any second copy");
        Assert.IsGreaterThan(
            0, record.VerifiedObjects, "the stamp claims a proof drawn from no objects at all");
    }

    [TestMethod]
    public async Task Pass_ABlobRottedAtThePeer_IsAFindingAndNotASuccess()
    {
        // The proof has to bite, or it is a stamp rather than a check. A
        // length-preserving corruption is the case a listing and a length can
        // never catch: only opening the record and failing its tag does.
        await SeedAsync();

        var replica = await ReplicaPathAsync();
        var blobs = Directory.GetFiles(Path.Combine(replica, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(blobs, "the fixture must have shipped a data blob to rot");
        foreach (var blob in blobs)
        {
            var bytes = await File.ReadAllBytesAsync(blob, Timeout);
            bytes[bytes.Length / 2] ^= 0xFF;
            await File.WriteAllBytesAsync(blob, bytes, Timeout);
        }

        _harness.WriteSourceFile("docs/second.txt", "a second capture, so the pass runs again");
        await RunPassAsync();

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreNotEqual(
            DestinationSyncState.InSync,
            record.State,
            "every data blob at the peer was corrupted and the pass called the destination in sync");
    }

    private async Task SeedAsync()
    {
        await StartDestinationAsync();
        WriteConfiguration();
        _harness.WriteSourceFile("docs/report.txt", new string('r', 200_000));

        await RunPassAsync();
        _ = await ReplicaPathAsync();
    }

    private async Task RunPassAsync()
    {
        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);
    }

    private async Task<string> ReplicaPathAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "snapshots")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the destination never took a replica");
            await Task.Delay(100, Timeout);
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
                Id = _harness.DocsSetId,
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                Schedule = "every 1h",
                Destinations = [new SetDestinationReference { Ref = "friend" }],
                DirectShip = true,
            },
        ],
    }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        _destinationIdentity = destinationKeypair.Identity;

        var destinationGrants = PeerGrantStore.Open(_destinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
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
