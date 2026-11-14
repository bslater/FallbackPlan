using System.Net;
using System.Net.Sockets;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// A TLS handshake that does not complete, at either end (specification
/// peer-protocol 02 §1): it is a transport failure, raised as an
/// <see cref="IOException"/> that says the handshake did not complete and why,
/// whichever end gave up and however the platform's TLS stack reported it.
/// </summary>
/// <remarks>
/// 02 §1 makes no trust decision during the handshake, so a handshake that
/// fails can only mean the connection could not be had. Raised as the
/// platform raises it, the same failure arrives as an authentication error on
/// one stack and an end-of-stream on another, and a caller that names a peer
/// unreachable on the one misses the other. The raw bytes here are what a
/// stranger, a scanner or a broken TLS stack would send, so no platform's
/// client or server is asked to misbehave.
/// </remarks>
[TestClass]
public sealed class PeerHandshakeFailureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    private CancellationToken Cancellation => _cts.Token;

    [TestMethod]
    public async Task TheResponder_AHelloItCannotAnswer_EndsAsAnIOException_SayingTheHandshakeDidNotComplete()
    {
        using var listener = Listen(out var endpoint);
        using var dialler = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await dialler.ConnectAsync(endpoint, Cancellation);
        var accepted = await listener.AcceptAsync(Cancellation);

        // Offers only a suite that needs an RSA key, which the responder's
        // throwaway ECDSA certificate cannot serve: no TLS stack can answer it.
        await dialler.SendAsync(UnanswerableTlsHello.ClientHello(), Cancellation);

        var failure = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await PeerTlsConnection.AcceptAsync(accepted, Now, Cancellation));
        Assert.Contains("TLS handshake did not complete", failure.Message, StringComparison.Ordinal);
        Assert.IsNotNull(failure.InnerException, "the platform's own account of the failure is kept");
        Assert.Contains(Innermost(failure.InnerException).Message, failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheInitiator_AResponderThatAnswersWithAnAlert_EndsAsAnIOException_SayingTheHandshakeDidNotComplete()
    {
        using var listener = Listen(out var endpoint);
        var answering = AnswerWithHandshakeFailureAsync(listener);

        var failure = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation));
        Assert.Contains("TLS handshake did not complete", failure.Message, StringComparison.Ordinal);
        Assert.IsNotNull(failure.InnerException, "the platform's own account of the failure is kept");
        await answering;
    }

    [TestMethod]
    public async Task TheInitiator_AResponderThatHangsUpMidHandshake_EndsAsAnIOException_SayingTheHandshakeDidNotComplete()
    {
        // The failure the drill met on Windows: the far end closed before the
        // handshake finished. It already arrives as an IOException; what it
        // gains is the sentence saying it was the handshake.
        using var listener = Listen(out var endpoint);
        var hangingUp = HangUpAsync(listener);

        var failure = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, Cancellation));
        Assert.Contains("TLS handshake did not complete", failure.Message, StringComparison.Ordinal);
        await hangingUp;
    }

    [TestMethod]
    public async Task TheInitiator_ACancelledHandshake_IsACancellation_NotAnUnreachablePeer()
    {
        // A responder that accepts and never answers holds the handshake open
        // until the caller gives up, which is the caller's decision and must
        // reach it as one.
        using var listener = Listen(out var endpoint);
        var accepted = listener.AcceptAsync(Cancellation).AsTask();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await PeerTlsConnection.DialAsync(
                endpoint.Address.ToString(), endpoint.Port, Now, cancelled.Token));
        (await accepted).Dispose();
    }

    /// <summary>A loopback listener on an OS-assigned port.</summary>
    private static Socket Listen(out IPEndPoint endpoint)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        endpoint = (IPEndPoint)listener.LocalEndPoint!;
        return listener;
    }

    /// <summary>
    /// Accepts one connection, reads the client's hello, and answers it with a
    /// fatal <c>handshake_failure</c> alert (RFC 5246 §7.2) before closing.
    /// </summary>
    private async Task AnswerWithHandshakeFailureAsync(Socket listener)
    {
        using var accepted = await listener.AcceptAsync(Cancellation);
        var buffer = new byte[16 * 1024];
        _ = await accepted.ReceiveAsync(buffer, Cancellation);
        await accepted.SendAsync(UnanswerableTlsHello.HandshakeFailureAlert(), Cancellation);
        accepted.Shutdown(SocketShutdown.Both);
    }

    /// <summary>Accepts one connection, reads the client's hello, and closes without a word.</summary>
    private async Task HangUpAsync(Socket listener)
    {
        using var accepted = await listener.AcceptAsync(Cancellation);
        var buffer = new byte[16 * 1024];
        _ = await accepted.ReceiveAsync(buffer, Cancellation);
        accepted.Shutdown(SocketShutdown.Both);
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();
}
