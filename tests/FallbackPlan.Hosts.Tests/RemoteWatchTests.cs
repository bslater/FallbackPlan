using System.Net;
using System.Net.Sockets;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Protocol;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The remote binding's watch ends when its service stops, as the local
/// binding's does (FR-SVC-005's progress half). A watch belongs to a console
/// that has already connected, so a service the watch cannot reach, loses in
/// the handshake, or loses part-way through a frame is a service that
/// stopped. A caller redials on the end; a throw would reach it as a failure
/// instead.
/// </summary>
/// <remarks>
/// The service is a stand-in speaking the real handshake — TLS, the peer
/// session, the command contract's hello — so that it can go away at a
/// chosen step every time. It answers the console's command connection as a
/// service does, and then does to the watch's connection whatever the test
/// says.
/// </remarks>
[TestClass]
public sealed class RemoteWatchTests : IDisposable
{
    private static readonly HelloAcknowledgementFrame Accepted =
        new(ContractVersion.Current.ToString(), Accepted: true, Message: null);

    private static readonly ProgressFrame Progress =
        new(new JobProgressEvent(1, new JobProgress("job-1", JobState.Scanning, 1, 0, 0, 0, 0, 0)));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-remote-watch-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(1));
    private readonly PeerKeypair _console = PeerKeypair.Generate();
    private readonly PeerKeypair _service = PeerKeypair.Generate();
    private readonly PeerGrantStore _consoleGrants;
    private readonly PeerGrantStore _serviceGrants;
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public RemoteWatchTests()
    {
        // Both sides pin the other — the state a completed pairing leaves.
        _serviceGrants = PeerGrantStore.Open(Path.Combine(_root, "service"));
        _serviceGrants.Pin(new PeerGrant(
            _console.Identity, "console", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));
        _consoleGrants = PeerGrantStore.Open(Path.Combine(_root, "console"));
        _consoleGrants.Pin(new PeerGrant(
            _service.Identity, "service", PeerRole.StoresForUs, PeerTerms.None, 1_722_600_000_000));

        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen();
    }

    private CancellationToken Timeout => _timeout.Token;

    [TestMethod]
    public async Task Watch_WhenTheServiceHasGoneBeforeItOpens_EndsRatherThanThrowing()
    {
        // The console connected, and then the service stopped and its port
        // closed: the watch dials a port nothing listens on.
        var (console, command) = await ConnectAsync();
        await using (console)
        await using (command)
        {
            _listener.Close();

            Assert.AreEqual(0, await CountWatchedAsync(console));
        }
    }

    [TestMethod]
    [DataRow("tls")]
    [DataRow("peer session")]
    [DataRow("hello")]
    public async Task Watch_WhenTheServiceGoesAwayInItsHandshake_EndsRatherThanThrowing(string step)
    {
        // The same stopped service, met one step later: it took the watch's
        // connection and went away before the step named, so the watch meets
        // a closed connection where it expected the service's half of it.
        var (console, command) = await ConnectAsync();
        await using (console)
        await using (command)
        {
            var watching = CountWatchedAsync(console);

            var socket = await _listener.AcceptAsync(Timeout);
            if (step == "tls")
            {
                socket.Dispose();
            }
            else
            {
                await using var connection = await PeerTlsConnection.AcceptAsync(socket, DateTimeOffset.UtcNow, Timeout);
                if (step == "hello")
                {
                    var session = await PeerSessionDriver.AcceptAsync(
                        connection, _service, _serviceGrants, "stand-in", cancellationToken: Timeout);
                    Assert.IsInstanceOfType<HelloFrame>(await FrameCodec.ReadAsync(session.Stream, Timeout));
                }
            }

            Assert.AreEqual(0, await watching);
        }
    }

    [TestMethod]
    [DataRow("acknowledgement")]
    [DataRow("progress")]
    public async Task Watch_WhenTheServiceStopsPartWayThroughAFrame_EndsRatherThanThrowing(string cutShort)
    {
        // A service that stops while it is writing leaves the watch a frame
        // whose length prefix promised more than the connection delivered.
        // That is the connection ending, whether it falls on the hello's
        // acknowledgement or on a progress frame later. A whole progress
        // frame goes first in the second case, so the count shows the watch
        // was streaming when the connection ended.
        var (console, command) = await ConnectAsync();
        await using (console)
        await using (command)
        {
            var watching = CountWatchedAsync(console);

            var (connection, stream) = await AcceptSessionAsync();
            await using (connection)
            {
                Assert.IsInstanceOfType<HelloFrame>(await FrameCodec.ReadAsync(stream, Timeout));
                if (cutShort == "acknowledgement")
                {
                    await WriteCutShortAsync(stream, Accepted);
                }
                else
                {
                    await FrameCodec.WriteAsync(stream, Accepted, Timeout);
                    Assert.IsInstanceOfType<WatchFrame>(await FrameCodec.ReadAsync(stream, Timeout));
                    await FrameCodec.WriteAsync(stream, Progress, Timeout);
                    await WriteCutShortAsync(stream, Progress);
                }
            }

            Assert.AreEqual(cutShort == "progress" ? 1 : 0, await watching);
        }
    }

    /// <summary>
    /// Connects the console, the stand-in answering its command connection
    /// as a service does and keeping it open until the caller disposes it.
    /// </summary>
    private async Task<(RemoteServiceClient Console, PeerTlsConnection Command)> ConnectAsync()
    {
        var answering = Task.Run(
            async () =>
            {
                var (connection, stream) = await AcceptSessionAsync();
                Assert.IsInstanceOfType<HelloFrame>(await FrameCodec.ReadAsync(stream, Timeout));
                await FrameCodec.WriteAsync(stream, Accepted, Timeout);
                return connection;
            },
            Timeout);

        var console = await RemoteServiceClient.ConnectAsync(
            IPAddress.Loopback.ToString(), ((IPEndPoint)_listener.LocalEndPoint!).Port,
            _console, _consoleGrants, _service.Identity, "console", Timeout);

        return (console, await answering);
    }

    /// <summary>Accepts one connection and opens the peer session on it as the service.</summary>
    private async Task<(PeerTlsConnection Connection, Stream Stream)> AcceptSessionAsync()
    {
        var socket = await _listener.AcceptAsync(Timeout);
        var connection = await PeerTlsConnection.AcceptAsync(socket, DateTimeOffset.UtcNow, Timeout);
        try
        {
            var session = await PeerSessionDriver.AcceptAsync(
                connection, _service, _serviceGrants, "stand-in", cancellationToken: Timeout);
            return (connection, session.Stream);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Watches to the end and says how many events arrived.</summary>
    private async Task<int> CountWatchedAsync(RemoteServiceClient console)
    {
        var delivered = 0;
        await foreach (var _ in console.WatchAsync(Timeout))
        {
            delivered++;
        }

        return delivered;
    }

    /// <summary>Writes the length prefix and half the payload of <paramref name="frame"/>.</summary>
    private async Task WriteCutShortAsync(Stream stream, WireFrame frame)
    {
        using var whole = new MemoryStream();
        await FrameCodec.WriteAsync(whole, frame, Timeout);
        var bytes = whole.ToArray();
        await stream.WriteAsync(bytes.AsMemory(0, 4 + ((bytes.Length - 4) / 2)), Timeout);
        await stream.FlushAsync(Timeout);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _console.Dispose();
        _service.Dispose();
        _timeout.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
