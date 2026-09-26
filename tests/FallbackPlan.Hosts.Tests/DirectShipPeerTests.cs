using System.Net;
using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;
using FallbackPlan.Recovery;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Lifecycle;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The peer write adapter: a direct-ship set (ADR-0046) ships its capture
/// straight to a peer destination over the replication exchange, instead of
/// being told that peer shipping follows later. Establishes the peer shape of
/// FR-DEST-013 and FR-DEST-015, the direct-ship shape of FR-REP-001, and
/// FR-VER-001's rule that a pass with no evidence independent of the
/// destination claims nothing.
/// </summary>
/// <remarks>
/// <para>
/// Before this suite a direct-ship set refused every peer destination in
/// <c>BeginRunAsync</c>, so a set whose only destination was a peer could not
/// capture at all — the run found nowhere to write and failed. The shape is
/// not exotic: a household that keeps its backups at a friend's house and
/// nowhere else is exactly the configuration the peer protocol exists for,
/// and direct-ship is the default for a new set.
/// </para>
/// <para>
/// The assertion that matters is the last one in each test: the standalone
/// recovery tool restores the captured file from the replica, with only the
/// passphrase. A replica that merely has the right object count
/// is not a backup.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DirectShipPeerTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-direct-peer", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private const ulong PairedAt = 1_722_600_000_000;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string MetadataRoot => Path.Combine(_harness.StateDirectory, "sets", _harness.DocsSetId);

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;

    private string DestinationAddress => $"{_endpoint!.Address}:{_endpoint.Port}";

    [TestMethod]
    public async Task DirectShipSet_ItsOnlyDestinationAPeer_CapturesStraightToTheReplica()
    {
        // No local path anywhere in this set: the peer is the whole of its
        // durability, which is the configuration the old refusal made
        // impossible.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: false);
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));

        await RunOnceAsync();

        // The agent kept the planning copy and no file content at all — the
        // direct-ship promise, unchanged by the destination's kind.
        Assert.IsTrue(File.Exists(Path.Combine(MetadataRoot, "repository-format")));
        var localBlobs = Path.Combine(MetadataRoot, "blobs");
        Assert.IsTrue(
            !Directory.Exists(localBlobs)
                || Directory.GetFiles(localBlobs, "*", SearchOption.AllDirectories).Length == 0,
            "the agent's state must hold no blob content");

        var replica = await ReplicaPathAsync();
        Assert.IsTrue(
            Directory.GetFiles(Path.Combine(replica, "blobs"), "*", SearchOption.AllDirectories).Length > 0,
            "the peer holds no blobs — the content never shipped");
        await AssertReplicaHoldsTheMetadataPlaneAsync(replica);

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreEqual(
            DestinationSyncState.InSync, record.State, $"state={record.State} error={record.LastError}");

        await AssertRestoresFromAsync(replica, "docs/report.txt", "the words worth keeping");
    }

    [TestMethod]
    public async Task DirectShipSet_APeerBesideALocalPath_ShipsToBoth()
    {
        // The mixed set: the two destination kinds advance independently and
        // each ends a whole repository, which is ADR-0046's fan-out promise
        // with one of the fans now being a peer.
        Directory.CreateDirectory(Vault);
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: true);
        _harness.WriteSourceFile("docs/report.txt", "kept in two places");

        await RunOnceAsync();

        var local = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        var replica = await ReplicaPathAsync();
        await AssertReplicaHoldsTheMetadataPlaneAsync(replica);

        var ledger = DestinationSyncStore.Open(_harness.StateDirectory);
        foreach (var name in new[] { "vault", "friend" })
        {
            var record = ledger.Find(_harness.DocsSetId, name);
            Assert.IsNotNull(record, $"'{name}' has no ledger row");
            Assert.AreEqual(
                DestinationSyncState.InSync, record.State, $"{name}: state={record.State} error={record.LastError}");
        }

        await AssertRestoresFromAsync(local, "docs/report.txt", "kept in two places");
        await AssertRestoresFromAsync(replica, "docs/report.txt", "kept in two places");
    }

    [TestMethod]
    public async Task DirectShipSet_ASecondCapture_AddsToTheReplicaWithoutDisturbingTheFirst()
    {
        // The incremental shape over a live session. The second run opens a
        // second exchange, learns from the inventory what the peer already has,
        // and ships only the new work — and both snapshots must still restore,
        // because a replica that holds the newest snapshot and has lost the
        // closure of an older one is a replica that lies about its history.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: false);
        _harness.WriteSourceFile("docs/report.txt", "the first draft");

        await RunOnceAsync();
        var replica = await ReplicaPathAsync();

        _harness.WriteSourceFile("docs/report.txt", "the second draft");
        _harness.WriteSourceFile("docs/appendix.txt", "and something new");
        await RunOnceAsync();

        await AssertReplicaHoldsTheMetadataPlaneAsync(replica);
        Assert.HasCount(
            2,
            Directory.GetFiles(Path.Combine(replica, "snapshots"), "*", SearchOption.AllDirectories),
            "the peer holds one snapshot, so the second capture shipped nothing or replaced the first");

        var listing = await RunRecoveryAsync(
            "snapshots", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        var snapshots = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();
        Assert.HasCount(2, snapshots);

        // Both, restored out of the peer's replica alone.
        var drafts = new List<string>();
        foreach (var snapshot in snapshots)
        {
            var into = Path.Combine(_harness.WorkPath, "recovered-" + snapshot[..8]);
            var restore = await RunRecoveryAsync(
                "restore", "--repo", replica, "--passphrase-env", _harness.PassphraseVariable,
                "--snapshot", snapshot, "--output", into);
            Assert.AreEqual(0, restore.ExitCode, restore.Error);
            drafts.Add(await File.ReadAllTextAsync(
                Path.Combine(into, "docs", "report.txt"), Timeout));
        }

        Assert.Contains("the first draft", drafts);
        Assert.Contains("the second draft", drafts);
    }

    [TestMethod]
    public async Task DirectShipSet_ThePeerIsUnreachable_TheLocalSiblingStillReceives()
    {
        // The drop rule, unchanged for a peer: one destination that cannot be
        // dialled is that destination's failure and never the capture's, while
        // a healthy sibling remains (ADR-0046 §3). The peer is declared at an
        // address nothing is listening on.
        Directory.CreateDirectory(Vault);
        var fingerprint = await StartDestinationAsync();
        var unreachable = new IPEndPoint(IPAddress.Loopback, _endpoint!.Port);
        await _listener!.DisposeAsync();
        _listener = null;

        WriteConfiguration(fingerprint, withVault: true, address: $"{unreachable.Address}:{unreachable.Port}");
        _harness.WriteSourceFile("docs/report.txt", "the sibling still gets it");

        await RunOnceAsync();

        var local = Assert.ContainsSingle(Directory.GetDirectories(Vault));
        await AssertRestoresFromAsync(local, "docs/report.txt", "the sibling still gets it");

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreNotEqual(
            DestinationSyncState.InSync, record.State, "a peer nothing answered for cannot be in sync");
        Assert.AreNotEqual(
            DestinationSyncState.NotSupported, record.State,
            "an unreachable peer is unreachable, never an incapacity of this build");
    }

    [TestMethod]
    public async Task DirectShipSet_AWholeSchedulerPass_LeavesThePeerHoldingWhatItWasSent()
    {
        await _harness.SetupAsync();
        // Capture is only the first half of a pass: the fan-out and the
        // verification cadence run behind it, both of them written against a
        // source that holds the bytes. A peer-only direct-ship set has no such
        // source — the peer holds the only copy — so this test exists to hold
        // the outcome that matters whatever those phases decide they can do:
        // the replica the capture shipped is still there afterwards, whole,
        // and the pair is not recorded as failed.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: false);
        _harness.WriteSourceFile("docs/report.txt", "one pass, end to end");

        var run = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--once");
        Assert.AreEqual(0, run.ExitCode, run.Error);

        var replica = await ReplicaPathAsync();
        await AssertReplicaHoldsTheMetadataPlaneAsync(replica);
        Assert.IsTrue(
            Directory.GetFiles(Path.Combine(replica, "blobs"), "*", SearchOption.AllDirectories).Length > 0,
            "the pass left the peer holding no blobs — something after the capture took them away");

        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.AreNotEqual(
            DestinationSyncState.Failed, record.State,
            $"the pass recorded the peer as failed: {record.LastError}");

        // And the proof. The fan-out phase behind the capture cannot challenge
        // this peer against this side's own bytes, because for this set there
        // are none — so it reads the replica back instead and authenticates a
        // record inside a sampled blob under the repository's own key, which
        // needs no second copy at all
        // ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md) §8).
        Assert.IsNotNull(
            record.VerifiedAt,
            "the pass left the only copy of this set's content unexamined");
        Assert.IsGreaterThan(0, record.VerifiedObjects);
        Assert.IsEmpty(
            FallbackPlan.Application.NoticeStore.Open(_harness.StateDirectory).Unacknowledged
                .Where(entry => entry.Message.Contains("unchecked", StringComparison.Ordinal)),
            "the set was proved, so nothing should be telling the operator it was not");

        await AssertRestoresFromAsync(replica, "docs/report.txt", "one pass, end to end");
    }

    [TestMethod]
    public async Task DirectShipSet_AnUpgradeRecordWrittenAtTheSource_ReachesTheReplicaOnTheNextPass()
    {
        // The descriptor cannot carry a format upgrade to a peer: 03 §5 has
        // the destination commit an object it lacks and keep the one it has,
        // and 06 §3 forbids deleting `repository-format` by instruction, so a
        // rewritten descriptor would reach no replica ever. An append-only
        // record needs no protocol change at all — the commit path validates
        // that the key parses and nothing else, so an unfamiliar immutable
        // object rides the exchange the blobs ride.
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatVersions.SealedDataPlane;
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: false);
        _harness.WriteSourceFile("docs/report.txt", "the first draft");
        await RunOnceAsync();

        var replica = await ReplicaPathAsync();
        var upgradeKey = FormatUpgradeRecordCodec.KeyFor(FallbackPlan.Domain.FormatVersions.RelocatableRecords);
        var replicaStore = new LocalFileSystemObjectStore(replica);
        Assert.IsNull(await ReadAsync(replicaStore, ObjectKey.Parse(upgradeKey)));

        await WriteUpgradeAsync(FallbackPlan.Domain.FormatVersions.RelocatableRecords);

        _harness.WriteSourceFile("docs/report.txt", "the second draft");
        var pass = await HostHarness.RunAsync(
            AgentHost.RunAsync,
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--once");
        Assert.AreEqual(0, pass.ExitCode, pass.Error);

        var carried = await ReadAsync(replicaStore, ObjectKey.Parse(upgradeKey));
        Assert.IsNotNull(carried, "the upgrade record never reached the peer's replica");
        Assert.AreEqual(
            FallbackPlan.Domain.FormatVersions.RelocatableRecords,
            FormatUpgradeRecordCodec.Decode(carried).Value.ToVersion);
    }

    /// <summary>
    /// Writes a signed upgrade record into this set's own metadata plane —
    /// which for a direct-ship set is the state directory — exactly as the
    /// service's own upgrade path will.
    /// </summary>
    private async Task WriteUpgradeAsync(ushort toVersion)
    {
        var store = new LocalFileSystemObjectStore(MetadataRoot);
        using var passphrase = Passphrase.Create(
            Environment.GetEnvironmentVariable(_harness.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        {
            await RepositoryLifecycle.WriteFormatUpgradeAsync(
                store, repository.Descriptor, repository.Credential, toVersion,
                new byte[16],
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Timeout);
        }
    }

    /// <summary>
    /// Every object the planning copy holds is at the peer, byte for byte —
    /// the metadata plane is what makes the replica openable on its own.
    /// </summary>
    [TestMethod]
    public async Task DirectShipSet_ItsRunAcknowledged_FilesAndCountsTheReceiptThePeerSigned()
    {
        // ADR-0064 left this as its own named follow-up: the adapter "receives
        // the receipt with the run's acknowledgement and reads only its
        // count", leaving the peer's strongest statement about what it holds
        // — made at the moment the capture reached it — on the floor, and the
        // pair counted only if a later sync pass happened to fall due.
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, withVault: false);
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");

        await RunOnceAsync();

        var filed = Assert.ContainsSingle(
            ReplicationReceiptStore.Open(_harness.StateDirectory).List());
        Assert.IsTrue(filed.Verified, filed.Problem);
        Assert.AreEqual(DeletionReceiptRole.Commander, filed.Role);
        Assert.AreEqual("docs", filed.Set);
        Assert.AreEqual("friend", filed.Destination);
        Assert.IsNotNull(filed.Receipt);
        Assert.AreEqual(fingerprint, filed.SignerFingerprint);
        Assert.IsTrue(
            filed.Receipt!.CommittedCount > 0,
            "the run shipped this capture, and the receipt is the peer's statement of committing it");
        Assert.IsTrue(filed.Receipt.HeldObjects >= filed.Receipt.CommittedCount);

        // The destination filed its own copy of the same exchange, and the two
        // sides' files share a name because they share a session and an issue
        // time — neither depends on the other's.
        var theirs = Assert.ContainsSingle(ReplicationReceiptStore.Open(_destinationState).List());
        Assert.AreEqual(Path.GetFileName(filed.Path), Path.GetFileName(theirs.Path));

        // Counted on the strength of what the peer signed for, without a sync
        // pass: a source cannot cheaply list a peer's replica, so the run's
        // own acknowledgement is the only measurement it will get today.
        var record = DestinationSyncStore.Open(_harness.StateDirectory).Find(_harness.DocsSetId, "friend");
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.MeasuredAt, "the pair is counted complete under the receipt it verified");
        Assert.IsTrue(record.HeldBytes > 0);
        Assert.AreEqual(record.OwedBytes, record.HeldBytes);
    }

    private async Task AssertReplicaHoldsTheMetadataPlaneAsync(string replicaPath)
    {
        var metadata = new LocalFileSystemObjectStore(MetadataRoot);
        var replica = new LocalFileSystemObjectStore(replicaPath);
        await foreach (var entry in metadata.ListAsync(ObjectPrefix.All, ListOptions.Default, Timeout))
        {
            var mine = await ReadAsync(metadata, entry.Key);
            var theirs = await ReadAsync(replica, entry.Key);
            Assert.IsNotNull(theirs, $"the replica is missing {entry.Key.Value}");
            Assert.IsTrue(
                mine.AsSpan().SequenceEqual(theirs),
                $"the replica's {entry.Key.Value} is not byte-identical to the planning copy");
        }
    }

    /// <summary>The headline: the standalone tool restores the file from this repository alone.</summary>
    private async Task AssertRestoresFromAsync(string repositoryPath, string relativePath, string expected)
    {
        var listing = await RunRecoveryAsync(
            "snapshots", "--repo", repositoryPath, "--passphrase-env", _harness.PassphraseVariable);
        Assert.AreEqual(0, listing.ExitCode, listing.Error);
        var snapshot = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var into = Path.Combine(_harness.WorkPath, "recovered-" + Guid.NewGuid().ToString("n")[..8]);
        var restore = await RunRecoveryAsync(
            "restore", "--repo", repositoryPath, "--passphrase-env", _harness.PassphraseVariable,
            "--snapshot", snapshot, "--output", into);
        Assert.AreEqual(0, restore.ExitCode, restore.Error);
        Assert.AreEqual(
            expected,
            await File.ReadAllTextAsync(Path.Combine(into, relativePath.Replace('/', Path.DirectorySeparatorChar)), Timeout));
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

    /// <summary>
    /// The replica directory, waited for: the destination finishes committing
    /// its side after the sender has already returned.
    /// </summary>
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

            Assert.IsTrue(
                DateTimeOffset.UtcNow < deadline,
                $"the peer holds {directories.Length} replica(s) and none of them is a whole repository");
            await Task.Delay(100, Timeout);
        }
    }

    private async Task RunOnceAsync()
    {
        await _harness.SetupAsync();
        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);

        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
            .WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private void WriteConfiguration(string fingerprint, bool withVault, string? address = null)
    {
        List<DestinationConfiguration> destinations =
        [
            new()
            {
                Id = new string('1', 32),
                Name = "friend",
                Kind = DestinationKind.Peer,
                Fingerprint = fingerprint,
                Endpoint = address ?? DestinationAddress,
                Priority = 5,
            },
        ];
        List<SetDestinationReference> references = [new() { Ref = "friend" }];
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
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    private async Task<string> StartDestinationAsync()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

        _destinationGrants = PeerGrantStore.Open(_destinationState);
        _destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));

        var sourceGrants = PeerGrantStore.Open(_harness.StateDirectory);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(_destinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, _destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: _destinationState);
        _listener.Bind(new UnusedService());
        _endpoint = _listener.Endpoint;

        await Task.CompletedTask;
        return destinationKeypair.Identity.Fingerprint;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunRecoveryAsync(params string[] args)
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var exit = await RecoveryHost.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
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
        ServiceRuntime.ArchiveFormatVersion = FallbackPlan.Domain.FormatLimits.FormatVersion;
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
