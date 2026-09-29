using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer's <c>transfer_limit</c> (NFR-PERF-013), which is the case the limit
/// exists for: the link is somebody else's. What the scheduler pushes to the
/// peer, what a direct-ship capture writes to it and what a drill reads back
/// from it are paced; a person's push is not.
/// </summary>
/// <remarks>
/// <para>
/// The three reach the peer three different ways — the replication session
/// the fan-out drives, the ship sink's push session, and the retrieval session
/// the drill restores through — so each has a case, because a limit that held
/// one of them would be the setting an operator thought they had and not the
/// one they got.
/// </para>
/// <para>
/// On the runtime's virtual clock, as in <see cref="BackgroundRateLimitTests"/>.
/// Does not establish FR-REP-001 or FR-DRL-002.
/// </para>
/// </remarks>
[TestClass]
public sealed class PeerRateLimitTests : IDisposable
{
    private const string Limit = "64 KiB/s";
    private const long LimitBytesPerSecond = 64 * 1024;
    private const ulong PairedAt = 1_722_600_000_000;

    private readonly HostHarness _harness = new();
    private readonly VirtualPacing _pacing = new();

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-rate", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private IPEndPoint? _endpoint;
    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;
    private PeerGrantStore? _destinationGrants;

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task AScheduledPush_ToALimitedPeer_IsPaced_AndThePeerTakesTheWholeReplica()
    {
        await using var runtime = await StartAsync(directShip: false);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        Assert.IsNotNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "friend")?.LastSuccessAt, "a paced push still finishes");

        var limiter = runtime.Pacing.ForDestination(FriendIn(runtime));
        Assert.IsNotNull(limiter);
        var blobs = PeerBlobBytes();
        Assert.IsGreaterThan(0L, blobs);
        Assert.IsGreaterThanOrEqualTo(blobs, limiter.BytesPaced, "every blob byte the peer holds crossed the limit");
        AssertPacedFor(blobs);
    }

    [TestMethod]
    public async Task APersonsPush_ToTheSamePeer_IsNotPaced()
    {
        await using var runtime = await StartAsync(directShip: false);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        // The control: the person's push reached the peer with bytes the
        // scheduled case paces.
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "friend")?.LastSuccessAt);
        Assert.IsGreaterThan(0L, PeerBlobBytes());

        Assert.AreEqual(0L, runtime.Pacing.ForDestination(FriendIn(runtime))!.BytesPaced);
        Assert.AreEqual(TimeSpan.Zero, _pacing.Waited);
    }

    [TestMethod]
    public async Task ADirectShipCapture_ToALimitedPeer_IsPaced()
    {
        await using var runtime = await StartAsync(directShip: true);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        Assert.AreEqual(1, pass.Ran);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        var limiter = runtime.Pacing.ForDestination(FriendIn(runtime));
        Assert.IsNotNull(limiter);
        var blobs = PeerBlobBytes();
        Assert.IsGreaterThan(0L, blobs, "the direct-ship run must have written blobs to the peer");
        Assert.IsGreaterThanOrEqualTo(blobs, limiter.BytesPaced);
        AssertPacedFor(blobs);
    }

    [TestMethod]
    public async Task ADrillOfALimitedPeer_IsPaced_AndAPersonsIsNot()
    {
        await using var runtime = await StartAsync(directShip: true, drillIntervalDays: 7);

        // Shipped by a person, so nothing is paced before the drills.
        var set = runtime.Configuration.BackupSets.Single();
        var shipped = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", shipped.Outcome, shipped.Detail);
        var limiter = runtime.Pacing.ForDestination(FriendIn(runtime))!;
        Assert.AreEqual(0L, limiter.BytesPaced);
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var person = await RecoveryDrillJob.RunAsync(runtime, set, "friend", nowMs, userInitiated: true, Timeout);
        Assert.IsNull(person.Failure, person.Failure);
        Assert.IsGreaterThan(0, person.Files, "the control: the drill reached files over the wire");
        Assert.AreEqual(0L, limiter.BytesPaced, "a person's drill is not paced");

        var scheduled = await RecoveryDrillJob.RunAsync(runtime, set, "friend", nowMs, userInitiated: false, Timeout);
        Assert.IsNull(scheduled.Failure, scheduled.Failure);
        Assert.IsGreaterThan(0L, limiter.BytesPaced, "a scheduled drill reads the peer's replica through the limit");
    }

    /// <summary>The virtual waits cover the bytes at the rate, less the one second's burst.</summary>
    private void AssertPacedFor(long bytes)
    {
        var owed = TimeSpan.FromSeconds(((double)bytes / LimitBytesPerSecond) - 1.0);
        Assert.IsGreaterThan(TimeSpan.Zero, owed, "the fixture must move more than one second's worth");
        Assert.IsGreaterThanOrEqualTo(
            owed - TimeSpan.FromMilliseconds(100), _pacing.Waited, $"{bytes} bytes at {Limit} were not paced");
    }

    private static DestinationConfiguration FriendIn(ServiceRuntime runtime) =>
        runtime.Configuration.FindDestination("friend")
        ?? throw new AssertFailedException("the fixture declares a destination named friend");

    private long PeerBlobBytes()
    {
        var replicas = Path.Combine(_destinationState, "replicas");
        return Directory.Exists(replicas)
            ? Directory.GetDirectories(replicas)
                .Select(replica => Path.Combine(replica, "blobs"))
                .Where(Directory.Exists)
                .SelectMany(blobs => Directory.GetFiles(blobs, "*", SearchOption.AllDirectories))
                .Sum(path => new FileInfo(path).Length)
            : 0;
    }

    private void WriteConfiguration(string fingerprint, bool directShip, int? drillIntervalDays) =>
        new ClientConfiguration
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
                    DrillIntervalDays = drillIntervalDays,
                    TransferLimit = Limit,
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
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync(bool directShip, int? drillIntervalDays = null)
    {
        var fingerprint = await StartDestinationAsync();
        WriteConfiguration(fingerprint, directShip, drillIntervalDays);

        // Random, so compression cannot shrink the replica below the bytes the
        // arithmetic is about.
        var path = _harness.WriteSourceFile("docs/content.bin", string.Empty);
        var content = new byte[256 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(path, content, Timeout);
        _harness.WriteSourceFile("docs/notes.txt", "a file small enough to ride in the burst");

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                PacingClock = _pacing.Clock,
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

    /// <summary>The Bind contract needs a service; the replication path never calls it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a replication peer must not reach the command surface");
    }

    /// <summary>A clock whose waits complete at once and are summed.</summary>
    private sealed class VirtualPacing
    {
        private readonly Lock _gate = new();
        private long _now;
        private long _waited;

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
                        _waited += wait.Ticks;
                    }

                    return Task.CompletedTask;
                });

        public PacingClock Clock { get; }

        public TimeSpan Waited
        {
            get
            {
                lock (_gate)
                {
                    return TimeSpan.FromTicks(_waited);
                }
            }
        }
    }
}
