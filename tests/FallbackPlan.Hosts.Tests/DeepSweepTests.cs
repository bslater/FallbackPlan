using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The deep sweep in a running service. A circuit that has begun is carried
/// on pass after pass until every stored blob has been read, and the interval
/// separates one finished circuit from the next (FR-VER-002). Damage it finds
/// at a local path is repaired from a copy proven sound first — the staging
/// archive, another local destination, or a paired peer over the retrieval
/// session — or, where none exists, left in place, named, and held against
/// the pair until it is gone (FR-VER-005, FR-VER-007).
/// </summary>
/// <remarks>
/// <para>
/// Each of these was a gap in merged code. The scheduler read one segment of
/// sixty-four blobs per interval, so a replica of ten thousand blobs took
/// three years to be read once. The sweep's notice asked for the damaged
/// objects to be "re-copied and re-verified" and the sync it promised as the
/// repair copied nothing, because the copier counts any key that is present as
/// held and a local store never overwrites. And that sync then recorded the
/// pair in sync over damage the ledger had just been told about. The last was
/// found once the circuit was carried on every pass: a blob that would not
/// read stopped its segment and moved nothing, so the next pass read the same
/// run up to it again, once a minute, saying nothing.
/// </para>
/// <para>
/// Segments are made small with <see cref="ReplicaSweepJob.SegmentBudget"/>,
/// and a blob made to refuse its reads with
/// <see cref="ReplicaSweepJob.ReplicaDecorator"/>, each set before the runtime
/// starts so the class runs beside the rest of the suite; the pass clock is
/// the test's, as it is the service's.
/// </para>
/// </remarks>
[TestClass]
public sealed class DeepSweepTests : IDisposable
{
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-deep-sweep-peer", Guid.NewGuid().ToString("n"));

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    private string Spare => Path.Combine(_harness.WorkPath, "spare");

    [TestMethod]
    public async Task Sweep_AReplicaLargerThanOneSegment_ClosesItsCircuitOverConsecutivePasses()
    {
        // One blob a segment, so the circuit needs as many passes as the
        // replica has blobs. Before, the second segment waited a whole
        // interval: a week, for a replica of two blobs.
        ReplicaSweepJob.SegmentBudget = 1;
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", "a second capture, so the replica spans more blobs");
        await BackUpAsync(runtime);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        var blobs = BlobFiles(ReplicaRoot(Vault)).Count;
        Assert.IsGreaterThanOrEqualTo(3, blobs, "the replica must span several blobs for segmenting to mean anything");

        var first = Row(runtime, "vault");
        Assert.IsNotNull(first.SweptAt, "the first segment ran in the pass that copied");
        Assert.IsNull(first.SweepCompletedAt, "one blob of several cannot close a circuit");

        // A pass a minute, as the service runs them.
        for (var minute = 1; minute <= blobs && Row(runtime, "vault").SweepCompletedAt is null; minute++)
        {
            await PassAsync(runtime, start.AddMinutes(minute));
        }

        var closed = Row(runtime, "vault");
        Assert.IsNotNull(
            closed.SweepCompletedAt,
            "a circuit that has begun must be carried on each pass until every blob has been read");
        Assert.IsNull(closed.SweepCursor);

        // Then the interval, measured from when the circuit closed.
        var closedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)closed.SweepCompletedAt.Value);
        await PassAsync(runtime, closedAt.AddDays(1));
        Assert.AreEqual(closed.SweptAt, Row(runtime, "vault").SweptAt, "a closed circuit rests for its interval");

        await PassAsync(runtime, closedAt.AddDays(ReplicaSweepJob.DefaultIntervalDays).AddMinutes(1));
        Assert.AreNotEqual(closed.SweptAt, Row(runtime, "vault").SweptAt, "past the interval the next circuit begins");
    }

    [TestMethod]
    public async Task Sweep_ARottedBlobAtALocalPath_IsReplacedFromTheStagingArchive()
    {
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);

        var (key, victim) = Rot(ReplicaRoot(Vault));
        var staged = await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout);

        var outcome = await SweepAsync(runtime, "vault");

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(1, outcome.Repaired);
        CollectionAssert.AreEqual(staged, await File.ReadAllBytesAsync(victim, Timeout), "the staging copy must be what landed");

        // FR-VER-005: the finding degrades the pair until a sync has looked
        // again — and with nothing outstanding, that sync puts it back.
        var found = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, found.State);
        Assert.IsNull(found.DamagedKeys, "a repaired object is not outstanding");

        var notice = DeepVerifyNotice(runtime);
        Assert.Contains("no longer match what was sealed", notice.Message, StringComparison.Ordinal);
        Assert.Contains("the staging archive", notice.Message, StringComparison.Ordinal);
        Assert.Contains("check the device", notice.Message, StringComparison.Ordinal);

        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime, "vault").State, Row(runtime, "vault").LastError);
    }

    [TestMethod]
    public async Task Sweep_ARepairedDestination_KeepsItsNoticeUntilSomeoneAcknowledgesIt()
    {
        // A clean circuit used to withdraw the finding. Once the repair is
        // immediate that would withdraw it before anyone had seen it, and a
        // disk that altered a backup once is worth a person's attention
        // however well the service tidied up after it.
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        Rot(ReplicaRoot(Vault));
        await SweepAsync(runtime, "vault");

        var clean = await SweepAsync(runtime, "vault");

        Assert.AreEqual(0, clean.Damaged, "the control: the second circuit is clean");
        Assert.IsTrue(clean.CompletedCircuit);
        Assert.IsNotNull(DeepVerifyNotice(runtime), "the finding stands until acknowledged");
    }

    [TestMethod]
    public async Task Sweep_ADirectShipSet_RepairsFromAnotherDestination_NeverFromTheDamagedOne()
    {
        // A direct-ship set reads a blob back from the highest-priority
        // destination holding it — here, the damaged one. A repair that asked
        // the set's own read path would copy the damage onto itself.
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);

        var (key, victim) = Rot(ReplicaRoot(Vault));
        var spareCopy = await File.ReadAllBytesAsync(PathOf(ReplicaRoot(Spare), key), Timeout);

        var outcome = await SweepAsync(runtime, "vault");

        Assert.AreEqual(1, outcome.Repaired);
        CollectionAssert.AreEqual(spareCopy, await File.ReadAllBytesAsync(victim, Timeout));
        Assert.Contains("destination 'spare'", DeepVerifyNotice(runtime).Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_ADirectShipSet_NamesTheDestinationItRepairedFrom()
    {
        // The other way round: the sound copy is the one the set's read path
        // would answer from first. A repair that asked that path would still
        // land the right bytes, and would then tell the person they came from
        // a staging archive this set does not have.
        await using var runtime = await StartAsync(directShip: true, ("vault", 1), ("spare", 10));
        await BackUpAsync(runtime);
        Rot(ReplicaRoot(Vault));

        var outcome = await SweepAsync(runtime, "vault");

        Assert.AreEqual(1, outcome.Repaired);
        var notice = DeepVerifyNotice(runtime).Message;
        Assert.Contains("destination 'spare'", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("staging", notice, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_ADirectShipSetWithAPeer_RepairsTheLocalPathFromThePeersReplica()
    {
        // The household shape: a drive by the machine and a friend's house.
        // When the drive rots, the only other copy is behind the wire, and the
        // retrieval session that serves a restore serves this too.
        var fingerprint = StartPeer();
        await using var runtime = await StartAsync(directShip: true, ("vault", null), peerFingerprint: fingerprint);
        await BackUpAsync(runtime);
        var peerReplica = await PeerReplicaAsync();

        var (key, victim) = Rot(ReplicaRoot(Vault));
        var peerCopy = await File.ReadAllBytesAsync(PathOf(peerReplica, key), Timeout);

        var outcome = await SweepAsync(runtime, "vault");

        Assert.AreEqual(1, outcome.Repaired, Row(runtime, "vault").LastError);
        CollectionAssert.AreEqual(peerCopy, await File.ReadAllBytesAsync(victim, Timeout));
        Assert.Contains("destination 'friend'", DeepVerifyNotice(runtime).Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sweep_NoSoundCopyAnywhere_DeletesNothing_AndThePairStaysFailedThroughABackupAndASync()
    {
        await using var runtime = await StartAsync(directShip: true, ("vault", null));
        await BackUpAsync(runtime);

        var (key, victim) = Rot(ReplicaRoot(Vault));
        var damaged = await File.ReadAllBytesAsync(victim, Timeout);

        var outcome = await SweepAsync(runtime, "vault");

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(0, outcome.Repaired);
        CollectionAssert.AreEqual(
            damaged, await File.ReadAllBytesAsync(victim, Timeout),
            "a damaged blob with no replacement keeps the records that still restore");

        var found = Row(runtime, "vault");
        Assert.AreEqual(key, Assert.ContainsSingle(found.DamagedKeys!));
        Assert.AreEqual(DestinationSyncState.Failed, found.State);
        Assert.Contains("no sound copy", DeepVerifyNotice(runtime).Message, StringComparison.Ordinal);

        // A capture ships its new blobs there and records a success that
        // knows nothing about the old ones.
        _harness.WriteSourceFile("docs/later.txt", "written after the damage was found");
        await BackUpAsync(runtime);
        Assert.AreEqual(DestinationSyncState.Failed, Row(runtime, "vault").State, "a capture's success does not unsay known damage");

        // And the sync that re-checks, with nothing to repair from, says so.
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));
        var after = Row(runtime, "vault");
        Assert.AreEqual(DestinationSyncState.Failed, after.State);
        Assert.Contains("no sound copy", after.LastError!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Sync_DamageTheSweepCouldNotRepair_IsRepairedOnceASoundCopyExists()
    {
        // The repair sync ADR-0035 promised, made real: it re-reads the keys
        // the ledger holds, repairs what it now can, and only then may call
        // the pair in sync. Here the only other copy was damaged too when the
        // sweep looked, and is whole again by the time the sync does.
        await using var runtime = await StartAsync(directShip: true, ("vault", 10), ("spare", 1));
        await BackUpAsync(runtime);

        var (key, victim) = Rot(ReplicaRoot(Vault));
        var spareVictim = PathOf(ReplicaRoot(Spare), key);
        var spareCopy = await File.ReadAllBytesAsync(spareVictim, Timeout);
        RotFile(spareVictim);

        var outcome = await SweepAsync(runtime, "vault");
        Assert.AreEqual(0, outcome.Repaired, "the control: nothing sound to repair from yet");
        Assert.IsNotNull(Row(runtime, "vault").DamagedKeys);

        await File.WriteAllBytesAsync(spareVictim, spareCopy, Timeout);
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));

        var repaired = Row(runtime, "vault");
        CollectionAssert.AreEqual(spareCopy, await File.ReadAllBytesAsync(victim, Timeout));
        Assert.IsNull(repaired.DamagedKeys);
        Assert.AreEqual(DestinationSyncState.InSync, repaired.State, repaired.LastError);
    }

    [TestMethod]
    public async Task VerifyDestination_ADamagedReplica_SaysWhatWasReplaced()
    {
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        Rot(ReplicaRoot(Vault));
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand(null, "vault", Full: true), Timeout),
            out var result);

        Assert.AreEqual(1L, result.Damaged, "found damage still fails the verification, repaired or not");
        var line = Assert.ContainsSingle(result.Lines);
        Assert.Contains("damaged object(s)", line, StringComparison.Ordinal);
        Assert.Contains("replaced", line, StringComparison.Ordinal);
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
            // Best effort: a test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task Sweep_ABlobThatWillNotRead_IsTriedAgainUnderBackOff_NotOnEveryPass()
    {
        // A circuit under way is due on every pass, and a segment that stopped
        // at a blob it could not read left its circuit under way — so a disk
        // with one bad sector was read up to it once a minute, for ever, and
        // nothing said so. The segment keeps what it read before the blob,
        // and the next attempt waits the sync's back-off.
        string? unreadable = null;
        ReplicaSweepJob.ReplicaDecorator = replica => new UnreadableObjectStore(replica, key => key == unreadable);
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await SyncAsync(runtime, "vault");
        var blobs = BlobKeys(ReplicaRoot(Vault));
        Assert.IsGreaterThanOrEqualTo(2, blobs.Count, "the replica must span several blobs");
        unreadable = blobs[1];

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);

        var stalled = Row(runtime, "vault");
        Assert.AreEqual(1, stalled.SweepStalls);
        Assert.AreEqual(unreadable, stalled.SweepStalledOn);
        Assert.AreEqual(blobs[0], stalled.SweepCursor, "what was read before the blob stands");
        Assert.AreNotEqual(DestinationSyncState.Failed, stalled.State, "a blob that would not be read has not been found altered");
        Assert.IsNull(stalled.DamagedKeys);

        await PassAsync(runtime, start.AddMinutes(1));
        Assert.AreEqual(stalled.SweepStalledAt, Row(runtime, "vault").SweepStalledAt, "not tried again on the next pass");

        await PassAsync(runtime, start.AddMinutes(3));
        var again = Row(runtime, "vault");
        Assert.AreEqual(2, again.SweepStalls, "tried again once the back-off had passed, and stopped at the same blob");
        Assert.AreEqual(blobs[0], again.SweepCursor);
    }

    [TestMethod]
    public async Task Sweep_ThreeStallsInARow_AreSaid_AndTheNoticeIsWithdrawnOnceASegmentGetsPast()
    {
        // Two could be a drive re-seated mid-read; three in a row, minutes
        // apart, is a device that will not give up a backup, which is a
        // person's to hear. It is a condition rather than a finding — nothing
        // is shown altered — so it is withdrawn once the sweep reads past it.
        string? unreadable = null;
        ReplicaSweepJob.ReplicaDecorator = replica => new UnreadableObjectStore(replica, key => key == unreadable);
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await SyncAsync(runtime, "vault");
        unreadable = BlobKeys(ReplicaRoot(Vault))[0];

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        await PassAsync(runtime, start.AddMinutes(3));
        Assert.IsNull(StallNotice(runtime), "two stalls are not yet said");
        await PassAsync(runtime, start.AddMinutes(8));

        Assert.AreEqual(3, Row(runtime, "vault").SweepStalls);
        var notice = StallNotice(runtime);
        Assert.IsNotNull(notice, "three stalls in a row are said");
        Assert.Contains("'vault'", notice.Message, StringComparison.Ordinal);
        Assert.Contains(unreadable, notice.Message, StringComparison.Ordinal);
        Assert.Contains(UnreadableObjectStore.Refusal, notice.Message, StringComparison.Ordinal);
        Assert.AreNotEqual(DestinationSyncState.Failed, Row(runtime, "vault").State);

        unreadable = null;
        await PassAsync(runtime, start.AddMinutes(17));

        var through = Row(runtime, "vault");
        Assert.AreEqual(0, through.SweepStalls);
        Assert.IsNotNull(through.SweepCompletedAt, "the circuit carried on past it");
        Assert.IsNull(StallNotice(runtime), "a condition that has cleared is withdrawn");
    }

    [TestMethod]
    public async Task VerifyDestination_ABlobThatWillNotRead_SaysWhereItStopped_AndCountsNoDamage()
    {
        string? unreadable = null;
        ReplicaSweepJob.ReplicaDecorator = replica => new UnreadableObjectStore(replica, key => key == unreadable);
        await using var runtime = await StartAsync(directShip: false, ("vault", null));
        await BackUpAsync(runtime);
        await SyncAsync(runtime, "vault");
        unreadable = BlobKeys(ReplicaRoot(Vault))[1];
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand(null, "vault", Full: true), Timeout),
            out var result);

        Assert.AreEqual(0L, result.Damaged, "a blob that would not be read has not been found altered");
        var line = Assert.ContainsSingle(result.Lines);
        Assert.Contains("1 object(s) confirmed", line, StringComparison.Ordinal);
        Assert.Contains(unreadable, line, StringComparison.Ordinal);
        Assert.Contains(UnreadableObjectStore.Refusal, line, StringComparison.Ordinal);
        Assert.DoesNotContain("every stored object has now been checked", line, StringComparison.Ordinal);
        Assert.AreEqual(1, Row(runtime, "vault").SweepStalls, "a person's attempt is an attempt, and a full pass stops at it");
    }

    private static DestinationSyncRecord Row(ServiceRuntime runtime, string destination) =>
        runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, destination)
        ?? throw new AssertFailedException($"'{destination}' has no ledger row");

    private static Notice DeepVerifyNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal))
        ?? throw new AssertFailedException("no deep-verification notice stands");

    private Task<(int Examined, int Damaged, int Repaired, string? Cursor, bool CompletedCircuit)> SweepAsync(
        ServiceRuntime runtime, string destination) =>
        ReplicaSweepJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(), destination,
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var pass = await Scheduler.RunPassAsync(runtime, now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    /// <summary>One sync and no sweep, so a test can choose what the sweep will meet.</summary>
    private async Task SyncAsync(ServiceRuntime runtime, string destination)
    {
        var sync = FanOut.Enqueue(
            runtime, runtime.Configuration.BackupSets.Single(), destination, DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync, "nothing else was syncing, so the sync cannot have been coalesced away");
        await sync.WaitAsync(Timeout);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime, destination).State, Row(runtime, destination).LastError);
    }

    private static Notice? StallNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key.StartsWith("deep-verify-stalled:", StringComparison.Ordinal));

    /// <summary>The replica's blob keys, in the order a circuit reads them.</summary>
    private static List<string> BlobKeys(string replicaRoot) =>
        [.. BlobFiles(replicaRoot)
            .Select(path => Path.GetRelativePath(replicaRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>The one repository directory a destination path holds.</summary>
    private static string ReplicaRoot(string destinationPath) =>
        Assert.ContainsSingle(Directory.GetDirectories(destinationPath));

    private static List<string> BlobFiles(string replicaRoot) =>
        [.. Directory.GetFiles(Path.Combine(replicaRoot, "blobs"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)];

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Flips one byte in the middle of the replica's first data blob.</summary>
    private static (string Key, string Path) Rot(string replicaRoot)
    {
        var victim = Directory
            .GetFiles(Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();
        RotFile(victim);
        return (Path.GetRelativePath(replicaRoot, victim).Replace(Path.DirectorySeparatorChar, '/'), victim);
    }

    private static void RotFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private async Task<ServiceRuntime> StartAsync(
        bool directShip, params (string Name, int? Priority)[] localPaths) =>
        await StartAsync(directShip, localPaths, peerFingerprint: null);

    private async Task<ServiceRuntime> StartAsync(
        bool directShip, (string Name, int? Priority) localPath, string? peerFingerprint) =>
        await StartAsync(directShip, [localPath], peerFingerprint);

    private async Task<ServiceRuntime> StartAsync(
        bool directShip, (string Name, int? Priority)[] localPaths, string? peerFingerprint)
    {
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));

        var destinations = new List<DestinationConfiguration>();
        var references = new List<SetDestinationReference>();
        var id = 1;
        foreach (var (name, priority) in localPaths)
        {
            var path = Path.Combine(_harness.WorkPath, name);
            Directory.CreateDirectory(path);
            destinations.Add(new DestinationConfiguration
            {
                Id = new string((char)('0' + id++), 32), Name = name, Kind = DestinationKind.LocalPath, Path = path,
                Priority = priority,
            });
            references.Add(new SetDestinationReference { Ref = name });
        }

        if (peerFingerprint is not null)
        {
            destinations.Add(new DestinationConfiguration
            {
                Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer,
                Fingerprint = peerFingerprint, Endpoint = $"{_listener!.Endpoint.Address}:{_listener.Endpoint.Port}",
            });
            references.Add(new SetDestinationReference { Ref = "friend" });
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
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the
                // destinations are told apart by name.
                VolumeIdentityOverride = path =>
                    path.Contains("vault", StringComparison.Ordinal) ? 2UL
                    : path.Contains("spare", StringComparison.Ordinal) ? 3UL
                    : 1UL,
            },
            Timeout);
    }

    /// <summary>A paired destination listening on loopback, as the peer suites stand one up.</summary>
    private string StartPeer()
    {
        using var sourceKeypair = PeerKeypairStore.Open(_harness.StateDirectory);
        using var destinationKeypair = PeerKeypairStore.Open(_destinationState);

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
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>
    /// The peer's replica directory, waited for: the destination finishes
    /// committing its side after the sender has already returned.
    /// </summary>
    private async Task<string> PeerReplicaAsync()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var directories = Directory.Exists(replicas) ? Directory.GetDirectories(replicas) : [];
            if (directories.Length == 1 && Directory.Exists(Path.Combine(directories[0], "blobs", "data")))
            {
                return directories[0];
            }

            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "the peer never held the set's blobs");
            await Task.Delay(100, Timeout);
        }
    }

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }
}
