using System.Net;
using System.Net.Sockets;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A TLS handshake that does not complete, met by the service at both ends of
/// a peer connection (specification peer-protocol 02 §1). The listener ends
/// that connection alone, says why in its log, and still stops cleanly
/// (FR-SVC-004: an unpaired or substituted client is refused, the service
/// untouched). A restore that opens a peer's replica names the peer not
/// reachable rather than failing with the platform's TLS error (FR-REP-001: a
/// replica a peer holds stays reachable by its owner, and when it is not, the
/// owner is told so).
/// </summary>
/// <remarks>
/// The bytes are a stranger's or a broken stack's (see
/// <see cref="UnanswerableTlsHello"/>), because no platform's own TLS client or
/// server can be asked to fail a handshake on purpose.
/// </remarks>
[TestClass]
public sealed class PeerHandshakeFailureTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private readonly string _peerState =
        Path.Combine(Path.GetTempPath(), "fbp-peer-handshake", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task TheListener_AHelloItCannotAnswer_EndsThatConnection_SaysWhy_AndStopsCleanly()
    {
        await _harness.SetupAsync();
        using var keypair = PeerKeypairStore.Open(_peerState);
        var log = new RecordingLogger();
        var listener = RemoteServiceListener.Start(
            keypair, PeerGrantStore.Open(_peerState), new IPEndPoint(IPAddress.Loopback, 0),
            "fallbackplan-agent/test", log, replicationStateDirectory: _peerState);
        var stopping = false;
        try
        {
            listener.Bind(new UnusedService());

            using (var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await stranger.ConnectAsync(listener.Endpoint, Timeout);
                await stranger.SendAsync(UnanswerableTlsHello.ClientHello(), Timeout);

                // The listener answers with an alert, or with nothing, and
                // closes: read until it has. A close with bytes unread is a
                // reset on some platforms, and is the same end.
                var buffer = new byte[4096];
                try
                {
                    while (await stranger.ReceiveAsync(buffer, Timeout) > 0)
                    {
                    }
                }
                catch (SocketException)
                {
                }
            }

            // The listener's own stop waits for every connection it served,
            // and must not carry one connection's failed handshake out with
            // it: the service stopping behind it would throw what a stranger
            // sent.
            stopping = true;
            await listener.DisposeAsync();
        }
        finally
        {
            if (!stopping)
            {
                await listener.DisposeAsync();
            }
        }

        var ended = log.Records.Where(IsTheHandshakeEnding).ToList();
        Assert.ContainsSingle(ended, "the connection's end is logged once, saying it was the handshake");
        Assert.Contains("TLS handshake did not complete", (string)ended[0].Value("Reason")!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OpeningAPeersReplica_WhoseHandshakeFails_SaysThePeerIsNotReachable()
    {
        await _harness.SetupAsync();
        using var refusing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        refusing.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        refusing.Listen(4);
        var endpoint = (IPEndPoint)refusing.LocalEndPoint!;
        var answering = RefuseEveryHelloAsync(refusing);

        using (var peerKeypair = PeerKeypairStore.Open(_peerState))
        {
            PeerGrantStore.Open(_harness.StateDirectory).Pin(new PeerGrant(
                peerKeypair.Identity, "friend", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
            WriteConfiguration(peerKeypair.Identity.Fingerprint, endpoint);
        }

        await using var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions { ArchivesRoot = _harness.ArchivesRoot, StateDirectory = _harness.StateDirectory },
            Timeout);
        // As the drill asks it: the service's own scope, which a person's
        // restore reaches only after the passphrase.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Service);

        var answer = await handler.ExecuteAsync(new OpenRestoreSourceCommand("docs", "friend"), Timeout);

        Assert.IsInstanceOfType<ServiceError>(answer, out var error, answer.GetType().Name);
        Assert.AreEqual(ServiceErrorReason.Unavailable, error.Reason, error.Message);
        Assert.Contains("Peer 'friend' is not reachable", error.Message, StringComparison.Ordinal);
        Assert.Contains("TLS handshake did not complete", error.Message, StringComparison.Ordinal);

        refusing.Close();
        await answering;
    }

    private static bool IsTheHandshakeEnding(RecordedLog record) =>
        string.Equals(record.EventName, "RemoteEnded", StringComparison.Ordinal)
        && record.Value("Reason") is string reason
        && reason.Contains("TLS handshake", StringComparison.Ordinal);

    /// <summary>
    /// Answers every hello it is sent with a fatal handshake_failure alert, as
    /// a responder whose TLS stack could not agree on anything would.
    /// </summary>
    private async Task RefuseEveryHelloAsync(Socket listener)
    {
        while (true)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(Timeout);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            using (accepted)
            {
                try
                {
                    var buffer = new byte[16 * 1024];
                    _ = await accepted.ReceiveAsync(buffer, Timeout);
                    await accepted.SendAsync(UnanswerableTlsHello.HandshakeFailureAlert(), Timeout);
                    accepted.Shutdown(SocketShutdown.Both);
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    // The dialler gave up first; that is its business.
                }
            }
        }
    }

    private void WriteConfiguration(string fingerprint, IPEndPoint endpoint) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new()
                {
                    Id = new string('1', 32),
                    Name = "friend",
                    Kind = DestinationKind.Peer,
                    Fingerprint = fingerprint,
                    Endpoint = $"{endpoint.Address}:{endpoint.Port}",
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
                    Destinations = [new() { Ref = "friend" }],
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    /// <summary>The Bind contract needs a service; a connection that never gets past TLS never reaches it.</summary>
    private sealed class UnusedService : IFallbackPlanService
    {
        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a connection that failed its handshake must not reach the command surface");

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a connection that failed its handshake must not reach the command surface");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
        try
        {
            if (Directory.Exists(_peerState))
            {
                Directory.Delete(_peerState, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
