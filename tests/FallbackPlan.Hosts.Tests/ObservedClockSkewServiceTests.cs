using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Observed clock skew, end to end (NFR-TIME-002, ADR-0077): a peer whose
/// clock runs three hours ahead signs its replication receipts by that clock;
/// the hub reads its own clock either side of the exchange and keeps the
/// difference on the pair's ledger row; and the next capture records it in
/// its manifest, where <c>list_snapshots</c> reads it back per snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Both hub paths are covered, because both exchange a receipt and they are
/// written separately: a staging set syncs to its peer after capture, and a
/// direct-ship set ships to it during capture. On both, the capture that
/// first meets the peer records nothing — its manifest is signed before the
/// peer answers — and the capture after it records what that exchange read.
/// </para>
/// <para>
/// The peer's clock is the listener's receipt clock, the one this side of the
/// protocol signs <c>issued_at</c> by. Nothing else about the peer is skewed,
/// which is the point: the hub learns the peer's clock from the receipt and
/// from nothing else.
/// </para>
/// </remarks>
[TestClass]
public sealed class ObservedClockSkewServiceTests : IDisposable
{
    private const ulong PairedAt = 1_722_600_000_000;
    private const long ThreeHours = 10_800_000;

    /// <summary>
    /// The reading's own uncertainty is half a loopback round trip; a
    /// generous bound keeps a loaded runner from failing what is right.
    /// </summary>
    private const long Slack = 5_000;

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private readonly string _destinationState =
        Path.Combine(Path.GetTempPath(), "fbp-clock-skew", Guid.NewGuid().ToString("n"));

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task AStagingSet_RecordsWhatItsLastSyncRead_InTheNextSnapshot()
    {
        await using var runtime = await StartAsync(directShip: false);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await BackUpAsync(runtime);
        await SyncAsync(runtime);

        var reading = Row(runtime).Clock;
        Assert.IsNotNull(reading, "a verified receipt is a reading of the peer's clock");
        AssertThreeHoursBehind(reading.SkewMilliseconds);

        _harness.WriteSourceFile("docs/second.txt", "a second capture, after the peer was met");
        await BackUpAsync(runtime);

        var snapshots = await SnapshotsAsync(handler);
        Assert.HasCount(2, snapshots);
        var (first, second) = (snapshots.MinBy(s => s.CapturedAt)!, snapshots.MaxBy(s => s.CapturedAt)!);
        Assert.IsNull(first.ObservedClockSkewMs, "the first capture had met no peer, and records no reading rather than a zero");
        AssertThreeHoursBehind(second.ObservedClockSkewMs!.Value);
    }

    [TestMethod]
    public async Task ADirectShipSet_RecordsWhatItsLastRunRead_InTheNextSnapshot()
    {
        await using var runtime = await StartAsync(directShip: true);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        await BackUpAsync(runtime);

        var reading = Row(runtime).Clock;
        Assert.IsNotNull(reading, "the run's acknowledgement carried a verified receipt");
        AssertThreeHoursBehind(reading.SkewMilliseconds);

        _harness.WriteSourceFile("docs/second.txt", "a second capture, after the peer was met");
        await BackUpAsync(runtime);

        var snapshots = await SnapshotsAsync(handler);
        Assert.HasCount(2, snapshots);
        Assert.IsNull(
            snapshots.MinBy(s => s.CapturedAt)!.ObservedClockSkewMs,
            "the first run's manifest was signed before its peer answered");
        AssertThreeHoursBehind(snapshots.MaxBy(s => s.CapturedAt)!.ObservedClockSkewMs!.Value);
    }

    private static void AssertThreeHoursBehind(long skew) =>
        Assert.IsTrue(
            Math.Abs(skew - ThreeHours) <= Slack,
            $"a peer three hours ahead reads as this clock three hours behind; read {skew} ms");

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "friend")
        ?? throw new AssertFailedException("the pair has no ledger row");

    private async Task<IReadOnlyList<SnapshotDescriptor>> SnapshotsAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        return listed.Snapshots;
    }

    private async Task BackUpAsync(ServiceRuntime runtime)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task SyncAsync(ServiceRuntime runtime)
    {
        var sync = FanOut.Enqueue(
            runtime, runtime.Configuration.BackupSets.Single(), "friend", DateTimeOffset.Now, userInitiated: true);
        Assert.IsNotNull(sync, "nothing else was syncing, so the sync cannot have been coalesced away");
        await sync.WaitAsync(Timeout);
        Assert.AreEqual(DestinationSyncState.InSync, Row(runtime).State, Row(runtime).LastError);
    }

    private async Task<ServiceRuntime> StartAsync(bool directShip)
    {
        _harness.WriteSourceFile("docs/report.txt", "the words worth keeping");
        var fingerprint = StartPeer(receiptClock: new OffsetClock(TimeSpan.FromMilliseconds(ThreeHours)));

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('f', 32), Name = "friend", Kind = DestinationKind.Peer,
                    Fingerprint = fingerprint, Endpoint = $"{_listener!.Endpoint.Address}:{_listener.Endpoint.Port}",
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
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
    }

    /// <summary>A paired destination listening on loopback, signing its receipts by <paramref name="receiptClock"/>.</summary>
    private string StartPeer(TimeProvider receiptClock)
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
            log: null, replicationStateDirectory: _destinationState, receiptClock: receiptClock);
        _listener.Bind(new UnusedService());
        return destinationKeypair.Identity.Fingerprint;
    }

    /// <summary>The wall clock, moved by a fixed amount: a machine whose clock was set wrong.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
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
            // Best effort.
        }
    }
}
