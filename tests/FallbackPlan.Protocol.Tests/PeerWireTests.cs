using System.Net;
using System.Net.Sockets;
using FallbackPlan.Application;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The protocol driven over a real loopback TCP+TLS connection, not two
/// objects in one process (specification peer-protocol 02): the handshake, the
/// refusal paths, and the man-in-the-middle it defeats, each proven on a
/// socket. These are the repository's first tests to bind a port; they bind
/// port 0 and read back the assignment, because the runners own ports of their
/// own and a fixed one would collide.
/// </summary>
[TestClass]
public sealed class PeerWireTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-wire-tests", Guid.NewGuid().ToString("n"));

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    private readonly PeerKeypair _service = PeerKeypair.Generate();
    private readonly PeerKeypair _console = PeerKeypair.Generate();

    private CancellationToken Cancellation => _cts.Token;

    private PeerGrantStore Store(string name) => PeerGrantStore.Open(Path.Combine(_stateDirectory, name));

    private static void Pin(PeerGrantStore store, PeerIdentity identity) =>
        store.Pin(new PeerGrant(identity, "a peer", PeerRole.Both, PeerTerms.None, 1_722_600_000_000));

    /// <summary>
    /// Binds a loopback listener on an OS-assigned port and returns it with its
    /// endpoint. The caller accepts; the dialler connects to the endpoint.
    /// </summary>
    private static (Socket Listener, IPEndPoint Endpoint) Listen()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        return (listener, (IPEndPoint)listener.LocalEndPoint!);
    }

    [TestMethod]
    public async Task PeerConnection_EachEnd_PresentsADistinctCertificate()
    {
        var (listener, endpoint) = Listen();
        using var _ = listener;

        var accept = AcceptConnectionAsync(listener);
        await using var dialled = await PeerTlsConnection.DialAsync(
            endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
        await using var accepted = await accept;

        // Each side made its own throwaway certificate, so the bindings cross:
        // my local is your remote, and no two connections share a key.
        CollectionAssert.AreEqual(dialled.LocalBinding, accepted.RemoteBinding);
        CollectionAssert.AreEqual(dialled.RemoteBinding, accepted.LocalBinding);
        CollectionAssert.AreNotEqual(dialled.LocalBinding, dialled.RemoteBinding);
    }

    [TestMethod]
    public async Task Session_TwoPairedPeersOverRealTls_AuthenticateAndOpen()
    {
        var serviceStore = Store("service");
        var consoleStore = Store("console");
        Pin(serviceStore, _console.Identity);
        Pin(consoleStore, _service.Identity);

        var (service, console) = await OpenSessionAsync(serviceStore, consoleStore, _service.Identity);

        // Both reached Open, each naming the other's grant and agreeing on the
        // version — over a socket, with bindings taken off real certificates.
        Assert.AreEqual(_console.Identity, service.Peer.Identity);
        Assert.AreEqual(_service.Identity, console.Peer.Identity);
        Assert.AreEqual(PeerSessionNegotiation.CurrentVersion, service.Version);
        Assert.AreEqual(PeerSessionNegotiation.CurrentVersion, console.Version);
    }

    [TestMethod]
    public async Task Session_TheAcceptersHelloCarriesPerPeerTerms_TheDiallerAdoptsThem()
    {
        var serviceStore = Store("service");
        var consoleStore = Store("console");
        // The service's grant for the console carries the terms IT set — the
        // per-peer form 05 §6 asks of a destination. The resolver runs only
        // after authentication, because a stranger has no grant to read.
        serviceStore.Pin(new PeerGrant(
            _console.Identity, "a peer", PeerRole.StoresHere, new PeerTerms(123_456, string.Empty, 4), 1_722_600_000_000));
        Pin(consoleStore, _service.Identity);

        var (service, console) = await OpenSessionAsync(
            serviceStore, consoleStore, _service.Identity, termsForPeer: grant => grant.Terms);

        Assert.IsNotNull(console.TheirTerms);
        Assert.AreEqual(123_456UL, console.TheirTerms.QuotaBytes);
        Assert.AreEqual(4U, console.TheirTerms.RetentionFloorGenerations);

        // The console offered none back — terms belong to the side lending
        // the disk (01 §4), and this dialler lends nothing.
        Assert.IsNull(service.TheirTerms);
    }

    [TestMethod]
    public async Task Session_TheDialledIdentityDiffers_IsRefusedAsIdentityChanged()
    {
        var serviceStore = Store("service");
        var consoleStore = Store("console");
        Pin(serviceStore, _console.Identity);
        Pin(consoleStore, _service.Identity);

        // The console dials expecting some OTHER key than the one that answers —
        // a re-keyed or impersonated service. This is a hard failure (01 §2.5),
        // not a prompt.
        using var someoneElse = PeerKeypair.Generate();
        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await OpenSessionAsync(serviceStore, consoleStore, someoneElse.Identity));

        Assert.AreEqual(PeerRefusalReason.IdentityChanged, refusal.Reason);
    }

    [TestMethod]
    public async Task Session_TheDiallerIsUnpaired_IsRefusedAsNotPaired()
    {
        var serviceStore = Store("service");
        var consoleStore = Store("console");
        // The console pins the service, but the service has no grant for the
        // console — the exit criterion, over the wire: an unpaired client is
        // refused.
        Pin(consoleStore, _service.Identity);

        var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await OpenSessionAsync(serviceStore, consoleStore, _service.Identity));

        Assert.AreEqual(PeerRefusalReason.NotPaired, refusal.Reason);
    }

    [TestMethod]
    public async Task Session_AManInTheMiddleRelaysValidProofs_IsCaughtByBothSides()
    {
        // Mallory terminates TLS on both sides and relays the protocol bytes
        // verbatim, so both real peers see genuine SessionAuth/Proof messages —
        // but each of Mallory's two connections has its own certificates, so
        // the binding hashes in the transcript do not match the connection the
        // proof was signed against. This is the attack channel binding exists
        // to defeat, run through real TLS this time rather than fill bytes.
        var serviceStore = Store("service");
        var consoleStore = Store("console");
        Pin(serviceStore, _console.Identity);
        Pin(consoleStore, _service.Identity);

        var (serviceListener, serviceEndpoint) = Listen();
        using var _ = serviceListener;

        // The console dials Mallory; Mallory dials the service.
        var (malloryFront, malloryFrontEndpoint) = Listen();
        using var __ = malloryFront;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await AcceptRawAsync(serviceListener), Now, Cancellation);
            return await PeerSessionDriver.AcceptAsync(
                connection, _service, serviceStore, "service/test", null, null, cancellationToken: Cancellation);
        });

        var consoleSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                malloryFrontEndpoint.Address.ToString(), malloryFrontEndpoint.Port, Now, Cancellation);
            return await PeerSessionDriver.DialAsync(
                connection, _console, consoleStore, _service.Identity, "console/test", null, Cancellation);
        });

        // Mallory: accept the console, dial the service, pump bytes both ways.
        var relay = Task.Run(async () =>
        {
            await using var fromConsole = await PeerTlsConnection.AcceptAsync(
                await AcceptRawAsync(malloryFront), Now, Cancellation);
            await using var toService = await PeerTlsConnection.DialAsync(
                serviceEndpoint.Address.ToString(), serviceEndpoint.Port, Now, Cancellation);

            var forward = Pump(fromConsole.Stream, toService.Stream);
            var backward = Pump(toService.Stream, fromConsole.Stream);
            await Task.WhenAll(forward, backward);
        });

        // Both real ends refuse: the proof each received was signed against the
        // OTHER connection's certificates.
        var serviceRefusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await serviceSide);
        var consoleRefusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await consoleSide);

        Assert.AreEqual(PeerRefusalReason.AuthenticationFailed, serviceRefusal.Reason);

        // The console is refused too. Its reason is AuthenticationFailed when it
        // reads the service's relayed refusal or fails its own verification
        // first; it can be Malformed when the closing connection reaches it
        // before that message does. Both are a refusal — the guarantee is that
        // the console never opens a session with a relay in the middle.
        Assert.IsTrue(
            consoleRefusal.Reason is PeerRefusalReason.AuthenticationFailed or PeerRefusalReason.Malformed,
            $"the console saw {consoleRefusal.Reason}");

        await IgnoreFaultAsync(relay);
    }

    [TestMethod]
    public async Task Pairing_BothHumansApproveOverTheWire_PinsEachOtherWithMatchingStrings()
    {
        var serviceStore = Store("service-pair");
        var consoleStore = Store("console-pair");

        var (listener, endpoint) = Listen();
        using var _ = listener;

        string? serviceSaw = null;
        string? consoleSaw = null;
        PeerRole? serviceShownRole = null;
        PeerRole? consoleShownRole = null;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await AcceptRawAsync(listener), Now, Cancellation);
            return await PairingCeremony.AcceptAsync(
                connection.Stream, _service, serviceStore, "service", PeerRole.StoresForUs, PeerTerms.None,
                (prospect, _) =>
                {
                    serviceSaw = prospect.ShortAuthenticationString;
                    serviceShownRole = prospect.TheirRoleForUs;
                    return ValueTask.FromResult(true);
                },
                1_722_600_000_000, Cancellation);
        });

        var consoleSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferAsync(
                connection.Stream, _console, consoleStore, "console", PeerRole.StoresHere,
                (prospect, _) =>
                {
                    consoleSaw = prospect.ShortAuthenticationString;
                    consoleShownRole = prospect.TheirRoleForUs;
                    return ValueTask.FromResult(true);
                },
                1_722_600_000_000, Cancellation);
        });

        var serviceResult = await serviceSide;
        var consoleResult = await consoleSide;

        // Both approved, both pinned, and — the load-bearing property — the two
        // humans were shown the same string.
        Assert.IsTrue(serviceResult.Approved);
        Assert.IsTrue(consoleResult.Approved);
        Assert.AreEqual(serviceSaw, consoleSaw);
        Assert.Contains(' ', serviceSaw!); // two groups of three
        Assert.AreEqual(_console.Identity, serviceStore.Find(_console.Identity)?.Identity);
        Assert.AreEqual(_service.Identity, consoleStore.Find(_service.Identity)?.Identity);

        // The declared storage roles crossed the wire and reached each human:
        // the service was told the console will record it stores-here, and the
        // console was told the service will record it stores-for-us — the same
        // roles each side then pinned (ADR-0030 Amendment 2).
        Assert.AreEqual(PeerRole.StoresHere, serviceShownRole);
        Assert.AreEqual(PeerRole.StoresForUs, consoleShownRole);
        Assert.AreEqual(PeerRole.StoresForUs, serviceStore.Find(_console.Identity)?.Role);
        Assert.AreEqual(PeerRole.StoresHere, consoleStore.Find(_service.Identity)?.Role);
    }

    [TestMethod]
    public async Task Pairing_OneHumanDeclines_PinsNothingOnEitherSide()
    {
        var serviceStore = Store("service-decline");
        var consoleStore = Store("console-decline");

        var (listener, endpoint) = Listen();
        using var _ = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await AcceptRawAsync(listener), Now, Cancellation);
            return await PairingCeremony.AcceptAsync(
                connection.Stream, _service, serviceStore, "service", PeerRole.StoresForUs, PeerTerms.None,
                (_, _) => ValueTask.FromResult(false), // the operator declines
                1_722_600_000_000, Cancellation);
        });

        var consoleSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PairingCeremony.OfferAsync(
                connection.Stream, _console, consoleStore, "console", PeerRole.StoresHere,
                (_, _) => ValueTask.FromResult(true),
                1_722_600_000_000, Cancellation);
        });

        var serviceResult = await serviceSide;
        var consoleResult = await consoleSide;

        Assert.IsFalse(serviceResult.Approved);
        Assert.IsFalse(consoleResult.Approved);
        Assert.IsNull(serviceStore.Find(_console.Identity));
        Assert.IsNull(consoleStore.Find(_service.Identity));
    }

    [TestMethod]
    public async Task PeerFrame_AHostileLengthPrefix_IsRefusedBeforeAllocating()
    {
        // A frame declaring more than the 16 MiB cap must be refused on the
        // declared length, before a byte of it is allocated (00 §2.3, T-7).
        // 17 MiB, never sent — just declared.
        var (listener, endpoint) = Listen();
        using var _ = listener;

        var reader = Task.Run(async () =>
        {
            using var accepted = await AcceptRawAsync(listener);
            await using var stream = new NetworkStream(accepted, ownsSocket: true);
            return await PeerFrame.ReadAsync(stream, Cancellation);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint, Cancellation);
        await using (var clientStream = client.GetStream())
        {
            var hostile = new byte[sizeof(uint)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hostile, 17u * 1024 * 1024);
            await clientStream.WriteAsync(hostile, Cancellation);
            await clientStream.FlushAsync(Cancellation);

            var refusal = await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await reader);
            Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
        }
    }

    private async Task<(PeerSession Service, PeerSession Console)> OpenSessionAsync(
        PeerGrantStore serviceStore, PeerGrantStore consoleStore, PeerIdentity consoleExpects,
        Func<PeerGrant, PeerTerms?>? termsForPeer = null)
    {
        var (listener, endpoint) = Listen();
        using var _ = listener;

        var serviceSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.AcceptAsync(
                await AcceptRawAsync(listener), Now, Cancellation);
            return await PeerSessionDriver.AcceptAsync(
                connection, _service, serviceStore, "service/test", null, termsForPeer,
                cancellationToken: Cancellation);
        });

        var consoleSide = Task.Run(async () =>
        {
            await using var connection = await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation);
            return await PeerSessionDriver.DialAsync(
                connection, _console, consoleStore, consoleExpects, "console/test", null, Cancellation);
        });

        // Wait for BOTH sides to settle before deciding what to surface. A
        // handshake refusal is raised on exactly one side; the other side merely
        // sees its peer drop the connection, which is an ordinary IOException and
        // not the guarantee under test. Which side refuses depends on the
        // scenario — the dialler on identity_changed, the accepter on not_paired
        // — so awaiting a fixed side first would surface the victim's IOException
        // in one of the two cases. Surface the protocol refusal in preference.
        var serviceOutcome = await CaptureAsync(serviceSide);
        var consoleOutcome = await CaptureAsync(consoleSide);

        if (serviceOutcome.Error is null && consoleOutcome.Error is null)
        {
            return (serviceOutcome.Result!, consoleOutcome.Result!);
        }

        var refusal = serviceOutcome.Error as PeerProtocolException
            ?? consoleOutcome.Error as PeerProtocolException;
        if (refusal is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(refusal);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
            serviceOutcome.Error ?? consoleOutcome.Error!);
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<(PeerSession? Result, Exception? Error)> CaptureAsync(Task<PeerSession> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private async Task<PeerTlsConnection> AcceptConnectionAsync(Socket listener) =>
        await PeerTlsConnection.AcceptAsync(await AcceptRawAsync(listener), Now, Cancellation);

    private async Task<Socket> AcceptRawAsync(Socket listener) =>
        await listener.AcceptAsync(Cancellation);

    private async Task Pump(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, Cancellation)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), Cancellation);
                await to.FlushAsync(Cancellation);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A relayed connection closing under us is the ordinary end of the
            // pump, not a test failure.
        }
    }

    private static async Task IgnoreFaultAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The caller is surfacing a different failure; this side's is noise.
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _service.Dispose();
        _console.Dispose();
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}
