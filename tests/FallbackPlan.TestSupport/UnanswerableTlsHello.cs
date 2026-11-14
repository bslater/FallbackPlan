namespace FallbackPlan.TestSupport;

/// <summary>
/// The raw bytes of a TLS hello no peer's responder can answer, and of the
/// alert that refuses one: what a stranger, a scanner or a broken TLS stack
/// puts on the wire, so a test can make a handshake fail without asking any
/// platform's own client or server to misbehave.
/// </summary>
public static class UnanswerableTlsHello
{
    /// <summary>
    /// A TLS 1.2 ClientHello (RFC 5246 §7.4.1.2) offering one cipher suite,
    /// TLS_RSA_WITH_AES_128_CBC_SHA, and no extensions. Its key exchange needs
    /// an RSA certificate and a peer responder's is ECDSA (peer-protocol 02
    /// §1), so no TLS stack can pick a suite both ends share.
    /// </summary>
    /// <returns>The whole record, ready to send.</returns>
    public static byte[] ClientHello()
    {
        List<byte> body = [0x03, 0x03];
        body.AddRange(Enumerable.Repeat((byte)0x5A, 32));
        body.Add(0x00);
        body.AddRange([0x00, 0x02, 0x00, 0x2F]);
        body.AddRange([0x01, 0x00]);

        List<byte> handshake = [0x01, 0x00, (byte)(body.Count >> 8), (byte)body.Count];
        handshake.AddRange(body);

        List<byte> record = [0x16, 0x03, 0x01, (byte)(handshake.Count >> 8), (byte)handshake.Count];
        record.AddRange(handshake);
        return [.. record];
    }

    /// <summary>A fatal <c>handshake_failure</c> alert record (RFC 5246 §7.2).</summary>
    /// <returns>The whole record, ready to send.</returns>
    public static byte[] HandshakeFailureAlert() => [0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28];
}
