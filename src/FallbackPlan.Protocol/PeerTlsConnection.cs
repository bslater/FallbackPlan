using Bodu;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace FallbackPlan.Protocol;

/// <summary>
/// One TLS 1.3 connection to a peer, and the two channel bindings the
/// authentication is bound to (specification peer-protocol 02 §1).
/// </summary>
/// <remarks>
/// <para>
/// The TLS here encrypts and authenticates <b>nobody</b>. Each side presents a
/// fresh self-signed certificate made for this connection and discarded with
/// it, and each side's validation callback accepts whatever the other
/// presents: 02 §1 makes no trust decision during the handshake, because the
/// only key that matters is the pinned peer key, checked afterwards in 02 §3.
/// A certificate here is a container for an ephemeral public key and nothing
/// more.
/// </para>
/// <para>
/// What the handshake produces that the protocol needs is the pair of
/// <see cref="LocalBinding"/> and <see cref="RemoteBinding"/> hashes — SHA-256
/// over each side's certificate <c>SubjectPublicKeyInfo</c> — which
/// <see cref="PeerAuthenticator"/> binds the real Ed25519 proof to. A relay
/// that terminated TLS on both sides would present its own certificates and so
/// change these hashes, which is what the binding exists to catch.
/// </para>
/// </remarks>
public sealed class PeerTlsConnection : IAsyncDisposable
{
    private readonly EphemeralTlsCertificate _certificate;
    private readonly SslStream _stream;

    private PeerTlsConnection(
        EphemeralTlsCertificate certificate,
        SslStream stream,
        PeerSessionRole role,
        byte[] localBinding,
        byte[] remoteBinding)
    {
        _certificate = certificate;
        _stream = stream;
        Role = role;
        LocalBinding = localBinding;
        RemoteBinding = remoteBinding;
    }

    /// <summary>The encrypted duplex stream the frames flow over.</summary>
    public Stream Stream => _stream;

    /// <summary>Which end of the connection this side is.</summary>
    public PeerSessionRole Role { get; }

    /// <summary>This side's channel binding — SHA-256 of the certificate it presented.</summary>
    public byte[] LocalBinding { get; }

    /// <summary>The peer's channel binding — SHA-256 of the certificate it presented.</summary>
    public byte[] RemoteBinding { get; }

    /// <summary>
    /// Wraps an accepted socket in TLS as the responder (server) end.
    /// </summary>
    /// <param name="socket">A freshly accepted, connected socket.</param>
    /// <param name="now">The clock, for the throwaway certificate's window.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The connection; dispose to close and destroy the ephemeral key.</returns>
    public static async ValueTask<PeerTlsConnection> AcceptAsync(
        Socket socket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(socket);
        return await EstablishAsync(socket, PeerSessionRole.Responder, host: null, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Dials a peer and wraps the connection in TLS as the initiator (client) end.
    /// </summary>
    /// <param name="host">The host to dial.</param>
    /// <param name="port">The port to dial.</param>
    /// <param name="now">The clock, for the throwaway certificate's window.</param>
    /// <param name="cancellationToken">Cancels the connect and handshake.</param>
    /// <returns>The connection; dispose to close and destroy the ephemeral key.</returns>
    public static async ValueTask<PeerTlsConnection> DialAsync(
        string host, int port, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(host);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return await EstablishAsync(socket, PeerSessionRole.Initiator, host, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
    }

    private static async ValueTask<PeerTlsConnection> EstablishAsync(
        Socket socket, PeerSessionRole role, string? host, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A day either side of now. 02 §1 makes no trust decision about this
        // certificate, so the window is read by nobody — but a validity that
        // straddles the moment keeps a strict TLS stack from rejecting it for
        // a reason that has nothing to do with what it is.
        var certificate = EphemeralTlsCertificate.Create(now.AddDays(-1), now.AddDays(1));
        SslStream? stream = null;

        // CA5359 warns that the validation callbacks accept any certificate.
        // That is the design, not an oversight: peer-protocol 02 §1 requires
        // that NO trust decision occur during the TLS handshake — the only key
        // that matters is the pinned peer key, checked afterwards in 02 §3
        // against a transcript bound to these very certificates. Validating a
        // name or chain here would be validating a throwaway self-signed cert
        // that authenticates nobody.
#pragma warning disable CA5359
        try
        {
            stream = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);

            // 1.2 alongside 1.3, on every platform (02 §1): one supported
            // platform's TLS stack cannot speak 1.3 at all, and negotiation
            // needs both ends to overlap, so pinning 1.3 anywhere means that
            // platform can peer with nobody. Two 1.3-capable ends still land
            // on 1.3 — the protocol's own downgrade sentinels see to it — and
            // no security property here derives from the version: identity is
            // the pinned-key proof of 02 §3, bound to these certificates.
            const SslProtocols AcceptedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

            if (role == PeerSessionRole.Responder)
            {
                await stream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate.Certificate,
                        // The peer must present a certificate — the binding needs
                        // one — but the platform validates none of it (02 §1).
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        EnabledSslProtocols = AcceptedProtocols,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        // A placeholder the server ignores; there is no name to
                        // validate on either side.
                        TargetHost = host ?? "fallbackplan-peer",
                        ClientCertificates = [certificate.Certificate],
                        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                        EnabledSslProtocols = AcceptedProtocols,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var remote = stream.RemoteCertificate
                ?? throw new PeerProtocolException(
                    PeerRefusalReason.AuthenticationFailed, "The peer presented no certificate to bind to.");

            using var remoteCertificate = new X509Certificate2(remote);
            var remoteBinding = EphemeralTlsCertificate.BindingOf(remoteCertificate);

            return new PeerTlsConnection(certificate, stream, role, certificate.PublicKeyHash, remoteBinding);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }

            certificate.Dispose();
            throw;
        }
#pragma warning restore CA5359
    }
}
