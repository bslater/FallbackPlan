using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer's replica re-read in full over the retrieval session, on a cadence
/// its source's operator states for it and never otherwise: every blob read
/// back and checked against the digest sealed into its own footer, a circuit
/// carried on every pass, a segment bounded by what the peer's link can spare.
/// What it finds is held against the pair until it has gone, and a peer that
/// will not serve the session is said to be unreadable, never blamed
/// (FR-VER-008, ADR-0035 Amendment 2).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can replace an object at a peer: the retrieval session reads
/// and a push only creates. So damage found there is named with the one remedy
/// that exists — the peer's owner removes the objects, and the next sync sends
/// them again whole — and the push that sends them is what clears them.
/// </para>
/// <para>
/// Does not establish FR-VER-007: nothing at a peer is repaired from here.
/// </para>
/// </remarks>
[TestClass]
public sealed class PeerDeepSweepTests : IDisposable
{
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly VirtualPacing _pacing = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-sweep", Guid.NewGuid().ToString("n"));

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task AScheduledPass_NeverSweepsAPeerWhoseOperatorStatedNoCadence()
    {
        // The drill's rule, for the drill's reason: re-reading all of a
        // replica is a standing cost on somebody else's link, and this
        // service does not put one there by default.
        await using var runtime = await StartAsync(cadenceDays: null);
        await BackUpAsync(runtime);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        await PassAsync(runtime, start.AddDays(10));

        Assert.IsNotNull(Row(runtime).LastSuccessAt, "the control: the peer holds the set");
        Assert.IsNull(Row(runtime).SweptAt, "a peer with no stated cadence is never swept");
    }

    [TestMethod]
    public async Task AScheduledPass_SweepsAPeerWithAStatedCadence_ThroughAWholeCircuit()
    {
        ReplicaSweepJob.SegmentBudget = 1;
        await using var runtime = await StartAsync(cadenceDays: 30);
        await BackUpAsync(runtime);
        _harness.WriteSourceFile("docs/second.txt", "a second capture, so the replica spans more blobs");
        await BackUpAsync(runtime);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);
        Assert.IsNotNull(Row(runtime).SweptAt, "the first segment ran in the pass that pushed");

        for (var minute = 1; minute <= 20 && Row(runtime).SweepCompletedAt is null; minute++)
        {
            await PassAsync(runtime, start.AddMinutes(minute));
        }

        var closed = Row(runtime);
        Assert.IsNotNull(closed.SweepCompletedAt, "the peer's circuit is carried on each pass until it closes");
        Assert.AreEqual(DestinationSyncState.InSync, closed.State, closed.LastError);
        Assert.IsFalse(
            runtime.Notices.Unacknowledged.Any(notice => notice.Key.StartsWith("deep-verify", StringComparison.Ordinal)),
            "an intact replica raises nothing");
    }

    [TestMethod]
    public async Task ASweep_FindsRotAtAPeersReplica_AndHoldsThePairFailedThroughASync()
    {
        await using var runtime = await StartAsync(cadenceDays: 30);
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        var (key, victim) = Rot(await PeerReplicaAsync());
        var damaged = await File.ReadAllBytesAsync(victim, Timeout);

        var outcome = await SweepAsync(runtime);

        Assert.AreEqual(1, outcome.Damaged);
        Assert.AreEqual(0, outcome.Repaired, "nothing here can replace an object at a peer");
        CollectionAssert.AreEqual(damaged, await File.ReadAllBytesAsync(victim, Timeout));

        var found = Row(runtime);
        Assert.AreEqual(key, Assert.ContainsSingle(found.DamagedKeys!));
        Assert.AreEqual(DestinationSyncState.Failed, found.State);

        // The notice names the objects and the one remedy there is.
        var notice = DeepVerifyNotice(runtime).Message;
        Assert.Contains("'friend'", notice, StringComparison.Ordinal);
        Assert.Contains("no longer match what was sealed", notice, StringComparison.Ordinal);
        Assert.Contains(key, notice, StringComparison.Ordinal);
        Assert.Contains("sends them again", notice, StringComparison.Ordinal);

        // A push succeeds around the damage and does not unsay it.
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));
        Assert.AreEqual(DestinationSyncState.Failed, Row(runtime).State, "the peer still holds what was found damaged");
    }

    [TestMethod]
    public async Task OnceThePeersOwnerRemovesTheDamagedObject_TheNextSyncSendsItAgain_AndThePairRecovers()
    {
        await using var runtime = await StartAsync(cadenceDays: 30);
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        var peerReplica = await PeerReplicaAsync();
        var (key, victim) = Rot(peerReplica);
        await SweepAsync(runtime);
        Assert.IsNotNull(Row(runtime).DamagedKeys, "the control: the damage is on the ledger");

        // The remedy the notice names, done by the peer's owner.
        File.Delete(victim);
        await PassAsync(runtime, DateTimeOffset.Now.AddHours(2));

        var recovered = Row(runtime);
        Assert.IsTrue(File.Exists(victim), "the push sends the removed object again");
        CollectionAssert.AreEqual(
            await File.ReadAllBytesAsync(PathOf(_harness.RepositoryPath, key), Timeout),
            await File.ReadAllBytesAsync(victim, Timeout),
            "what arrives is the staging archive's copy");
        Assert.IsNull(recovered.DamagedKeys, "the push that re-sent the object is what clears it");
        Assert.AreEqual(DestinationSyncState.InSync, recovered.State, recovered.LastError);
    }

    [TestMethod]
    public async Task VerifyDestination_APeer_ReadsItsWholeReplicaOverTheRetrievalSession()
    {
        // A person asking is consent for the read, as a restore is: no
        // cadence is needed, and the answer is the local path's answer.
        await using var runtime = await StartAsync(cadenceDays: null);
        await BackUpAsync(runtime);
        await PassAsync(runtime, DateTimeOffset.Now);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand(null, "friend", Full: true), Timeout),
            out var result);

        Assert.AreEqual(0L, result.Damaged);
        var line = Assert.ContainsSingle(result.Lines);
        Assert.Contains("object(s) confirmed", line, StringComparison.Ordinal);
        Assert.Contains("every stored object has now been checked", line, StringComparison.Ordinal);
        Assert.IsNotNull(Row(runtime).SweepCompletedAt);
    }

    [TestMethod]
    public async Task APeerThatWillNotServeTheSession_IsNotBlamed_SaysSo_AndIsNotDialledEveryPass()
    {
        await using var runtime = await StartAsync(
            cadenceDays: 30,
            offeredFeatures: [.. PeerSessionNegotiation.SupportedFeatures.Where(feature =>
                feature != PeerSessionNegotiation.RetrievalFeature)]);
        await BackUpAsync(runtime);

        var start = DateTimeOffset.Now;
        await PassAsync(runtime, start);

        var tried = Row(runtime);
        Assert.AreNotEqual(DestinationSyncState.Failed, tried.State, "a peer that cannot be read has not been found wanting");
        Assert.IsNull(tried.DamagedKeys);
        Assert.IsNotNull(tried.SweptAt, "the attempt is stamped, so the next waits its interval");
        var notice = runtime.Notices.Unacknowledged.SingleOrDefault(entry =>
            entry.Key.StartsWith("deep-verify-unavailable:", StringComparison.Ordinal));
        Assert.IsNotNull(notice, "a cadence that reads nothing must be said, not silently skipped");
        Assert.Contains("'friend'", notice.Message, StringComparison.Ordinal);

        await PassAsync(runtime, start.AddMinutes(2));
        Assert.AreEqual(tried.SweptAt, Row(runtime).SweptAt, "not redialled on every pass");

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<VerifyDestinationResult>(
            await handler.ExecuteAsync(new VerifyDestinationCommand(null, "friend", Full: false), Timeout),
            out var result);
        Assert.AreEqual(0L, result.Damaged);
        Assert.Contains("not deeply verifiable", Assert.ContainsSingle(result.Lines), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AScheduledSegmentOfALimitedPeer_ReadsAboutAMinutesWorth_AndAPersonsReadsThrough()
    {
        // As at a local path, and more so: the worker a segment holds is the
        // one every other transfer waits on, and at a peer's limit sixty-four
        // blobs is most of a day.
        WriteRandomSourceFile("docs/large.bin", 8 * 1024 * 1024);
        await using var runtime = await StartAsync(cadenceDays: 30, transferLimit: "64 KiB/s");
        await BackUpAsync(runtime);
        var person = await PersonPassThenSweepAsync(runtime);
        Assert.IsTrue(person.CompletedCircuit, "the control: a person's segment reads the whole small replica");

        var scheduled = await SweepAsync(runtime);

        Assert.IsFalse(scheduled.CompletedCircuit, "a minute at the peer's limit is less than this replica");
        Assert.IsNotNull(scheduled.Cursor);
        Assert.IsLessThan(person.Examined, scheduled.Examined);
        Assert.IsGreaterThanOrEqualTo(1, scheduled.Examined);
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

    private static DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(runtime.Configuration.BackupSets.Single().Id, "friend")
        ?? throw new AssertFailedException("'friend' has no ledger row");

    private static Notice DeepVerifyNotice(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.SingleOrDefault(notice =>
            notice.Key.StartsWith("deep-verify-failed:", StringComparison.Ordinal))
        ?? throw new AssertFailedException("no deep-verification notice stands");

    private Task<(int Examined, int Damaged, int Repaired, string? Cursor, bool CompletedCircuit)> SweepAsync(
        ServiceRuntime runtime) =>
        ReplicaSweepJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(), "friend",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: false, Timeout);

    /// <summary>A person's pass reaches the peer unpaced; then a person's segment reads it.</summary>
    private async Task<(int Examined, int Damaged, int Repaired, string? Cursor, bool CompletedCircuit)>
        PersonPassThenSweepAsync(ServiceRuntime runtime)
    {
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        return await ReplicaSweepJob.RunAsync(
            runtime, runtime.Configuration.BackupSets.Single(), "friend",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), userInitiated: true, Timeout);
    }

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

    private static string PathOf(string root, string key) =>
        Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Flips one byte in the middle of the replica's first data blob.</summary>
    private static (string Key, string Path) Rot(string replicaRoot)
    {
        var victim = Directory
            .GetFiles(Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();
        var bytes = File.ReadAllBytes(victim);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(victim, bytes);
        return (Path.GetRelativePath(replicaRoot, victim).Replace(Path.DirectorySeparatorChar, '/'), victim);
    }

    private void WriteRandomSourceFile(string relativePath, int length)
    {
        var path = _harness.WriteSourceFile(relativePath, string.Empty);
        var content = new byte[length];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
    }

    private async Task<ServiceRuntime> StartAsync(
        int? cadenceDays, string? transferLimit = null, IReadOnlyList<string>? offeredFeatures = null)
    {
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        _harness.WriteSourceFile("docs/big.bin", new string('b', 300_000));
        var fingerprint = StartPeer(offeredFeatures);

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer,
                    Fingerprint = fingerprint, Endpoint = $"{_listener!.Endpoint.Address}:{_listener.Endpoint.Port}",
                    DeepVerifyIntervalDays = cadenceDays, TransferLimit = transferLimit,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Destinations = [new SetDestinationReference { Ref = "friend" }],
                    DirectShip = false,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                PacingClock = _pacing.Clock,
            },
            Timeout);
    }

    /// <summary>A paired destination listening on loopback, as the peer suites stand one up.</summary>
    private string StartPeer(IReadOnlyList<string>? offeredFeatures)
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
            log: null, replicationStateDirectory: _destinationState, offeredFeatures: offeredFeatures);
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

    /// <summary>
    /// A clock whose waits complete at once and advance virtual time by what
    /// was asked, so a limited peer's segments cost no wall time.
    /// </summary>
    private sealed class VirtualPacing
    {
        private readonly Lock _gate = new();
        private long _now;

        public VirtualPacing() =>
            Clock = new PacingClock(
                () =>
                {
                    lock (_gate)
                    {
                        return _now;
                    }
                },
                (wait, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _now += wait.Ticks;
                    }

                    return Task.CompletedTask;
                });

        public PacingClock Clock { get; }
    }
}
