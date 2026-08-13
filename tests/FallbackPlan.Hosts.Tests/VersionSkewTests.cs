using System.Net;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Version skew refuses cleanly, never mid-transfer (FR-REP-004,
/// NFR-COMP-006). The negotiation halves — version ranges that do not
/// overlap, required features the peer lacks — are pinned in
/// <c>Protocol.Tests/PeerSessionTests</c>; this suite covers the half that
/// only shows over a real exchange: a replication offer whose repository
/// format capability this build does not implement is refused with a stated
/// reason before a single object crosses.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class VersionSkewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-version-skew", Guid.NewGuid().ToString("n"));

    private const ulong PairedAt = 1_722_600_000_000;

    private string SourceState => Path.Combine(_root, "source");
    private string DestinationState => Path.Combine(_root, "destination");

    private RemoteServiceListener? _listener;
    private PeerKeypair? _listenerKeypair;

    [TestMethod]
    public async Task ReplicationOffer_AFormatCapabilityThisBuildDoesNotSpeak_IsRefusedBeforeAnyTransfer()
    {
        Directory.CreateDirectory(SourceState);
        Directory.CreateDirectory(DestinationState);

        using var sourceKeypair = PeerKeypairStore.Open(SourceState);
        using var destinationKeypair = PeerKeypairStore.Open(DestinationState);

        var destinationGrants = PeerGrantStore.Open(DestinationState);
        destinationGrants.Pin(new PeerGrant(
            sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        var sourceGrants = PeerGrantStore.Open(SourceState);
        sourceGrants.Pin(new PeerGrant(
            destinationKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listenerKeypair = PeerKeypairStore.Open(DestinationState);
        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: DestinationState);
        _listener.Bind(new UnusedService());

        await using var connection = await PeerTlsConnection.DialAsync(
            _listener.Endpoint.Address.ToString(), _listener.Endpoint.Port,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var session = await PeerSessionDriver.DialAsync(
            connection, sourceKeypair, sourceGrants, destinationKeypair.Identity,
            "fallbackplan-agent/test", terms: null, CancellationToken.None);

        // A future build's repository format, offered to this one: the
        // session opened cleanly, so the refusal must be a stated reason on
        // the wire — never a mid-transfer failure or an undefined state.
        await PeerFrame.WriteAsync(
            session.Stream, new ReplicationOffer(new byte[16], FormatCapability: 999, "all"), CancellationToken.None);

        var frame = await PeerFrame.ReadAsync(session.Stream, CancellationToken.None);
        Assert.IsNotNull(frame);
        Assert.AreEqual(PeerMessageType.SessionRefuse, frame.Value.Type);
        var refusal = SessionRefuse.Read(frame.Value.Body);
        Assert.AreEqual(PeerRefusalReason.FeatureUnsupported, refusal.Reason);
        Assert.Contains("format capability", refusal.Text, StringComparison.OrdinalIgnoreCase);

        // Nothing was stored: the destination never created a replica for
        // the refused offer.
        Assert.IsFalse(
            Directory.Exists(Path.Combine(DestinationState, "replicas"))
            && Directory.GetDirectories(Path.Combine(DestinationState, "replicas")).Length > 0,
            "a refused offer must leave no replica behind");
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
