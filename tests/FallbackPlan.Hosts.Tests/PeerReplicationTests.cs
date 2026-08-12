using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Recovery;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Peer replication end to end (peer-protocol 03): a source pushes its
/// repository's objects to a paired destination over the peer session, and the
/// destination ends holding a replica a real restore reconstructs from — the
/// Phase-2 peer criterion (a source destroyed and recovered from its
/// destination) in its first concrete form. The role a grant carries decides
/// whether a peer replicates or commands; the exchange is idempotent on re-run.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PeerReplicationTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-replication-dest", Guid.NewGuid().ToString("n"));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private const ulong PairedAt = 1_722_600_000_000;

    [TestMethod]
    public async Task Replicate_ToAPairedDestination_MirrorsEveryObjectAndRecoversFromTheReplica()
    {
        await _source.CreateRepositoryAsync();
        _source.WriteSourceFile("notes.txt", "hello from the source");
        await _source.BackUpAsync();

        // The recovery kit is how a source destroyed and rebuilt from its
        // destination opens the replica — the bare store carries the objects,
        // the kit carries the wrapped keys (architecture 08 §5).
        var kit = await _source.ExportKitAsync();

        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);

        var replicate = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory,
            "--to", DestinationAddress, "--fingerprint", destinationFingerprint);
        Assert.AreEqual(0, replicate.ExitCode, replicate.Error);

        // The replica holds every object the source did, byte for byte.
        var replicaPath = Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single();
        var source = await ReadAllAsync(new LocalFileSystemObjectStore(_source.RepositoryPath));
        var replica = await ReadAllAsync(new LocalFileSystemObjectStore(replicaPath));
        Assert.AreEqual(source.Count, replica.Count, "the replica holds a different number of objects");
        foreach (var (key, bytes) in source)
        {
            Assert.IsTrue(replica.TryGetValue(key, out var copied), $"the replica is missing {key}");
            Assert.IsTrue(bytes.SequenceEqual(copied!), $"the replica's {key} differs from the source's");
        }

        // The headline: the standalone recovery tool restores from the replica —
        // a repository the destination holds but cannot read — with only the kit
        // and the passphrase, no catalogue and no state. This is the Phase-2 peer
        // criterion: a source destroyed and recovered from its destination.
        var listing = await RunRecoveryAsync(
            "snapshots", "--repo", replicaPath, "--kit", kit, "--passphrase-env", _source.PassphraseVariable);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        var snapshot = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var destination = Path.Combine(_source.WorkPath, "recovered");
        var restore = await RunRecoveryAsync(
            "restore", "--repo", replicaPath, "--kit", kit, "--passphrase-env", _source.PassphraseVariable,
            "--snapshot", snapshot, "--output", destination);
        Assert.AreEqual(0, restore.ExitCode, restore.Error);

        Assert.AreEqual(
            "hello from the source",
            await File.ReadAllTextAsync(Path.Combine(destination, "notes.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task Replicate_RunTwice_SendsNothingTheSecondTime()
    {
        await _source.CreateRepositoryAsync();
        _source.WriteSourceFile("notes.txt", "hello");
        await _source.BackUpAsync();

        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);

        var first = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory,
            "--to", DestinationAddress, "--fingerprint", destinationFingerprint);
        Assert.AreEqual(0, first.ExitCode, first.Error);

        // The destination already holds everything, so the second run commits 0
        // — the idempotent re-run that makes resumption need no checkpoint (03 §5).
        var second = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory,
            "--to", DestinationAddress, "--fingerprint", destinationFingerprint);
        Assert.AreEqual(0, second.ExitCode, second.Error);
        Assert.Contains("0 newly sent object(s)", second.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Replicate_ToAPeerPinnedAsAConsole_IsRefused()
    {
        await _source.CreateRepositoryAsync();
        _source.WriteSourceFile("notes.txt", "hello");
        await _source.BackUpAsync();

        // The destination pins the source as a console (stores-for-us), so it
        // runs the command pump, not the replication responder — the source's
        // replication offer is not answered as replication.
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresForUs);

        var replicate = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory,
            "--to", DestinationAddress, "--fingerprint", destinationFingerprint);

        Assert.AreEqual(1, replicate.ExitCode);
    }

    [TestMethod]
    public async Task Replicate_WithoutADestination_IsRefused()
    {
        var result = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory, "--fingerprint", "abcdef");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("host:port", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Replicate_WithoutAFingerprint_IsRefused()
    {
        var result = await RunAgentAsync(
            "replicate", "--repo", _source.RepositoryPath, "--state", _source.StateDirectory, "--to", "127.0.0.1:9");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--fingerprint", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task FanOut_APeerDestination_ConvergesWithoutACommand()
    {
        // No `replicate` verb anywhere in this test: the set declares a peer
        // destination and the scheduler pass fans out to it (ADR-0034 §3,
        // FR-DEST-002) — the hub doing automatically what the verb did by hand.
        _source.WriteSourceFile("notes.txt", "fan me out");
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);

        new FallbackPlan.Application.ClientConfiguration
        {
            SchemaVersion = FallbackPlan.Application.ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new FallbackPlan.Application.DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "friend",
                    Kind = FallbackPlan.Application.DestinationKind.Peer,
                    Fingerprint = destinationFingerprint,
                    Endpoint = DestinationAddress,
                },
            ],
            BackupSets =
            [
                new FallbackPlan.Application.BackupSetConfiguration
                {
                    Id = _source.DocsSetId,
                    Name = "docs",
                    Root = _source.SourceRoot,
                    Schedule = "every 4h",
                    Destinations = [new FallbackPlan.Application.SetDestinationReference { Ref = "friend" }],
                },
            ],
        }.Save(Path.Combine(_source.StateDirectory, "config.json"));

        var run = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        // The peer holds a byte-identical replica of the set's staging
        // archive, and the ledger says so durably (FR-DEST-004).
        var replicaPath = Directory.GetDirectories(Path.Combine(_destinationState, "replicas")).Single();
        var staging = await ReadAllAsync(new LocalFileSystemObjectStore(_source.RepositoryPath));
        var replica = await ReadAllAsync(new LocalFileSystemObjectStore(replicaPath));
        Assert.AreEqual(staging.Count, replica.Count);
        foreach (var (key, bytes) in staging)
        {
            Assert.IsTrue(replica.TryGetValue(key, out var copied), $"the replica is missing {key}");
            Assert.IsTrue(bytes.SequenceEqual(copied!), $"the replica's {key} differs from the source's");
        }

        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(FallbackPlan.Application.DestinationSyncState.InSync, record.State);
    }

    [TestMethod]
    public async Task Unpair_WithTheDestinationReachable_LeavesADurableNoticeOnBothEnds()
    {
        // The ending is announced while the grant still authenticates the
        // session, and both households keep a durable record of it
        // (FR-DEST-008, ADR-0030 Amendment 2).
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);

        var unpair = await RunAgentAsync(
            "unpair", "--state", _source.StateDirectory,
            "--fingerprint", destinationFingerprint, "--to", DestinationAddress);
        Assert.AreEqual(0, unpair.ExitCode, unpair.Error);
        Assert.Contains("notified", unpair.Output, StringComparison.Ordinal);

        // The destination: a notice that survives restarts (re-opened store),
        // and the source's grant gone. The listener handles the frame after
        // the sender has already returned, so this waits rather than races.
        await WaitForAsync(() =>
            FallbackPlan.Application.NoticeStore.Open(_destinationState).Unacknowledged.Count > 0);
        var destinationNotice = Assert.ContainsSingle(
            FallbackPlan.Application.NoticeStore.Open(_destinationState).Unacknowledged);
        Assert.Contains("ended the peering", destinationNotice.Message, StringComparison.Ordinal);
        using (var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory))
        {
            Assert.IsNull(_destinationGrants!.Find(sourceKeypair.Identity));
        }

        // The source: its own grant revoked, tombstoned rather than merely
        // forgotten — a dialler is later told `revoked`, not `not_paired`.
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);
        var sourceGrants = PeerGrantStore.Open(_source.StateDirectory);
        Assert.IsNull(sourceGrants.Find(destinationKeypair.Identity));
        Assert.IsTrue(sourceGrants.IsRevoked(destinationKeypair.Identity));
    }

    [TestMethod]
    public async Task FanOut_ThePeerRevokedThisHub_RaisesADurableNoticeFromTheRefusal()
    {
        // The other delivery path (FR-DEST-008): the spoke ended the peering
        // while the hub was away, so the hub learns from the Revoked refusal
        // at its next sync — and keeps the fact durably.
        _source.WriteSourceFile("notes.txt", "fan me out");
        var destinationFingerprint = await StartDestinationAsync(PeerRole.StoresHere);

        using (var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory))
        {
            // Through the listener's own store instance: revocation takes
            // effect at the next session (01 §3), and the listener resolves
            // grants from the store it was started with.
            _destinationGrants!.Revoke(sourceKeypair.Identity);
        }

        new FallbackPlan.Application.ClientConfiguration
        {
            SchemaVersion = FallbackPlan.Application.ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new FallbackPlan.Application.DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "friend",
                    Kind = FallbackPlan.Application.DestinationKind.Peer,
                    Fingerprint = destinationFingerprint,
                    Endpoint = DestinationAddress,
                },
            ],
            BackupSets =
            [
                new FallbackPlan.Application.BackupSetConfiguration
                {
                    Id = _source.DocsSetId,
                    Name = "docs",
                    Root = _source.SourceRoot,
                    Schedule = "every 4h",
                    Destinations = [new FallbackPlan.Application.SetDestinationReference { Ref = "friend" }],
                },
            ],
        }.Save(Path.Combine(_source.StateDirectory, "config.json"));

        // Exit 0: the backup itself succeeded; the destination's state is the
        // ledger's and the notice's to carry.
        var run = await RunAgentAsync(
            "run", "--archives", _source.ArchivesRoot, "--state", _source.StateDirectory,
            "--passphrase-env", _source.PassphraseVariable, "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var record = FallbackPlan.Application.DestinationSyncStore.Open(_source.StateDirectory)
            .Find(_source.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            FallbackPlan.Application.DestinationSyncState.Failed, record.State,
            $"state={record.State} error={record.LastError}");
        Assert.Contains("Revoked", record.LastError!, StringComparison.Ordinal);

        var notice = Assert.ContainsSingle(
            FallbackPlan.Application.NoticeStore.Open(_source.StateDirectory).Unacknowledged);
        Assert.Contains("ended the peering", notice.Message, StringComparison.Ordinal);
    }

    private IPEndPoint? _endpoint;
    private string DestinationAddress => $"{_endpoint!.Address}:{_endpoint.Port}";

    /// <summary>
    /// Stands up the destination: its own peer identity, a grant for the source
    /// with the given role, and a remote-binding listener serving replication.
    /// Pins the source into the destination and the destination into the source,
    /// and returns the destination's fingerprint.
    /// </summary>
    private async Task<string> StartDestinationAsync(PeerRole destinationRoleForSource)
    {
        using var sourceKeypair = PeerKeypairStore.Open(_source.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", destinationRoleForSource, PeerTerms.None, PairedAt));

        var sourceGrants = PeerGrantStore.Open(_source.StateDirectory);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        var listenerKeypair = PeerKeypairStore.Open(_destinationState);
        var listener = RemoteServiceListener.Start(
            listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        listener.Bind(new UnusedService());
        _endpoint = listener.Endpoint;
        _stop = new Stopper(listener, listenerKeypair);

        await Task.CompletedTask;
        return destinationKeypair.Identity.Fingerprint;
    }

    private Stopper? _stop;
    private PeerGrantStore? _destinationGrants;

    /// <summary>Polls until the condition holds — the listener handles a frame after the sender has already returned.</summary>
    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the condition did not hold within ten seconds");
            await Task.Delay(50, _timeout.Token);
        }
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAgentAsync(params string[] args) =>
        RunAsync(AgentHost.RunAsync, args);

    private static Task<(int ExitCode, string Output, string Error)> RunRecoveryAsync(params string[] args) =>
        RunAsync(RecoveryHost.RunAsync, args);

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> host, string[] args)
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var exit = await host(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<Dictionary<string, byte[]>> ReadAllAsync(LocalFileSystemObjectStore store)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer);
            contents[entry.Key.Value] = buffer.ToArray();
        }

        return contents;
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
        if (_stop is not null)
        {
            _stop.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _timeout.Dispose();
        _source.Dispose();
        if (Directory.Exists(_destinationState))
        {
            try
            {
                Directory.Delete(_destinationState, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
