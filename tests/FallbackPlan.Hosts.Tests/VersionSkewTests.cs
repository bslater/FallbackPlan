using System.Net;
using System.Security.Cryptography;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Version skew refuses cleanly, never mid-transfer (FR-REP-004,
/// NFR-COMP-006). The negotiation arithmetic — version ranges that do not
/// overlap, required features the peer lacks — is pinned in
/// <c>Protocol.Tests/PeerSessionTests</c>; this suite covers what only shows
/// over a real exchange between two differently-capable builds:
/// <list type="bullet">
/// <item>a replication offer whose repository format capability this build
/// does not implement is refused before a single object crosses;</item>
/// <item>a payload gated on a feature the destination never offered — a
/// verification challenge (04 §1) — is refused rather than answered, which
/// is what makes a negotiated feature set mean anything.</item>
/// </list>
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
    private PeerKeypair? _sourceKeypair;

    [TestMethod]
    public async Task ReplicationOffer_AFormatCapabilityThisBuildDoesNotSpeak_IsRefusedBeforeAnyTransfer()
    {
        var (sourceKeypair, sourceGrants, destinationIdentity) = StartDestination();

        await using var connection = await PeerTlsConnection.DialAsync(
            _listener!.Endpoint.Address.ToString(), _listener.Endpoint.Port,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var session = await PeerSessionDriver.DialAsync(
            connection, sourceKeypair, sourceGrants, destinationIdentity,
            "fallbackplan-agent/test", terms: null, cancellationToken: CancellationToken.None);

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

    [TestMethod]
    public async Task VerificationChallenge_ToAPeerThatNeverOfferedTheFeature_IsRefusedRatherThanAnswered()
    {
        // The destination half of the older-peer path (peer-protocol 04 §1).
        // The listener here offers everything except `destination-verification`
        // — what a build predating the feature announces — so the feature is
        // absent from the intersection and a challenge is not permitted. A
        // well-behaved source therefore cannot reach this guard at all
        // (FanOut skips sampling entirely), which is why the source below
        // misbehaves deliberately: the guard exists for a buggy or hostile
        // one, and an unenforced guard is not a guard.
        var (sourceKeypair, sourceGrants, destinationIdentity) =
            StartDestination(offeredFeatures: FeaturesWithoutVerification);

        await using var connection = await PeerTlsConnection.DialAsync(
            _listener!.Endpoint.Address.ToString(), _listener.Endpoint.Port,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var session = await PeerSessionDriver.DialAsync(
            connection, sourceKeypair, sourceGrants, destinationIdentity,
            "fallbackplan-agent/test", terms: null, cancellationToken: CancellationToken.None);

        Assert.IsFalse(
            session.Supports(PeerSessionNegotiation.DestinationVerificationFeature),
            "the destination withheld the feature, so the intersection must not carry it");

        // A challenge is only permitted after the acknowledgement (04 §1), so
        // the exchange has to get that far first. It carries no objects — the
        // point is the frame that comes next, not the transfer.
        var repositoryId = RandomNumberGenerator.GetBytes(16);
        await PeerFrame.WriteAsync(
            session.Stream, new ReplicationOffer(repositoryId, FormatCapability: 1, "all"), CancellationToken.None);
        await ReadInventoryAsync(session.Stream);
        await PeerFrame.WriteAsync(session.Stream, new ReplicationComplete(0), CancellationToken.None);

        var ack = await PeerFrame.ReadAsync(session.Stream, CancellationToken.None);
        Assert.IsNotNull(ack);
        Assert.AreEqual(PeerMessageType.ReplicationAck, ack.Value.Type, "the empty push must be acknowledged");

        await PeerFrame.WriteAsync(
            session.Stream,
            new VerificationChallenge(
                repositoryId, "blobs/data/aa/one", Offset: 0, Length: 16,
                new byte[VerificationChallenge.NonceLength], new byte[VerificationChallenge.ChallengeKeyLength]),
            CancellationToken.None);

        // Refused with a stated reason, not answered and not silently
        // ignored — and never a proof, which is the failure that would
        // matter: a destination that answers a challenge it never agreed to
        // is a destination whose feature set means nothing.
        var frame = await PeerFrame.ReadAsync(session.Stream, CancellationToken.None);
        Assert.IsNotNull(frame);
        Assert.AreNotEqual(PeerMessageType.VerificationProof, frame.Value.Type);
        Assert.AreEqual(PeerMessageType.SessionRefuse, frame.Value.Type);
        var refusal = SessionRefuse.Read(frame.Value.Body);
        Assert.AreEqual(PeerRefusalReason.FeatureUnsupported, refusal.Reason);
        Assert.Contains("destination-verification", refusal.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a build predating destination verification offers: everything in
    /// the 02 §6 registry except the one feature under test.
    /// </summary>
    private static readonly string[] FeaturesWithoutVerification =
    [
        PeerSessionNegotiation.RetentionInstructionFeature,
        PeerSessionNegotiation.TerminationNoticeFeature,
    ];

    /// <summary>
    /// Stands the destination up on a loopback port with reciprocal pinned
    /// grants, and hands back what the source needs to dial it.
    /// </summary>
    /// <param name="offeredFeatures">
    /// What the destination announces; null offers everything this build
    /// supports, which is what a current peer does.
    /// </param>
    private (PeerKeypair Source, PeerGrantStore SourceGrants, Protocol.PeerIdentity Destination) StartDestination(
        IReadOnlyList<string>? offeredFeatures = null)
    {
        Directory.CreateDirectory(SourceState);
        Directory.CreateDirectory(DestinationState);

        _sourceKeypair = PeerKeypairStore.Open(SourceState);
        _listenerKeypair = PeerKeypairStore.Open(DestinationState);

        var destinationGrants = PeerGrantStore.Open(DestinationState);
        destinationGrants.Pin(new PeerGrant(
            _sourceKeypair.Identity, "source", PeerRole.StoresHere, PeerTerms.None, PairedAt));
        var sourceGrants = PeerGrantStore.Open(SourceState);
        sourceGrants.Pin(new PeerGrant(
            _listenerKeypair.Identity, "destination", PeerRole.StoresForUs, PeerTerms.None, PairedAt));

        _listener = RemoteServiceListener.Start(
            _listenerKeypair, destinationGrants, new IPEndPoint(IPAddress.Loopback, 0), "fallbackplan-agent/test",
            log: null, replicationStateDirectory: DestinationState, offeredFeatures: offeredFeatures);
        _listener.Bind(new UnusedService());

        return (_sourceKeypair, sourceGrants, _listenerKeypair.Identity);
    }

    /// <summary>Reads the destination's inventory to its last page (03 §4).</summary>
    private static async Task ReadInventoryAsync(Stream stream)
    {
        while (true)
        {
            var frame = await PeerFrame.ReadAsync(stream, CancellationToken.None);
            Assert.IsNotNull(frame);
            Assert.AreEqual(PeerMessageType.ReplicationInventory, frame.Value.Type);
            if (!ReplicationInventory.Read(frame.Value.Body).More)
            {
                return;
            }
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

    public void Dispose()
    {
        _listener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _listenerKeypair?.Dispose();
        _sourceKeypair?.Dispose();
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
