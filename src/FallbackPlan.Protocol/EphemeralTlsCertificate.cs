using Bodu;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FallbackPlan.Protocol;

/// <summary>
/// The throwaway certificate a session's TLS handshake needs
/// (specification peer-protocol 02 §1).
/// </summary>
/// <remarks>
/// <para>
/// It authenticates nothing. The platform's TLS will not open a connection
/// without a certificate, so one is made for the connection and discarded with
/// it; neither side builds a chain, checks a name, or consults an authority.
/// The only durable thing about it is the hash of its public key, which
/// <see cref="SessionBinding"/> binds the real proof to.
/// </para>
/// <para>
/// <b>Never persisted, never reused.</b> 02 §1 requires that, and the reason it
/// is a rule rather than an optimisation is that reuse would let one
/// connection's binding describe another. A peer cannot check that the other
/// side obeyed — it would have to remember every certificate it had ever seen —
/// which is exactly why the transcript also carries nonces.
/// </para>
/// <para>
/// P-256 rather than Ed25519 because the platform cannot build or consume an
/// Ed25519 certificate, and one major TLS provider will not accept one. The
/// curve is of no consequence here: this key proves possession of itself for
/// the length of one connection and says nothing about who is holding it.
/// </para>
/// </remarks>
public sealed class EphemeralTlsCertificate : IDisposable
{
    private readonly ECDsa _key;
    private bool _disposed;

    private EphemeralTlsCertificate(ECDsa key, X509Certificate2 certificate)
    {
        _key = key;
        Certificate = certificate;
        SubjectPublicKeyInfo = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        PublicKeyHash = SessionBinding.TlsPublicKeyHash(SubjectPublicKeyInfo);
    }

    /// <summary>The certificate to present. Valid for this connection and no other.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The DER <c>SubjectPublicKeyInfo</c> the binding hashes.</summary>
    public byte[] SubjectPublicKeyInfo { get; }

    /// <summary>SHA-256 of <see cref="SubjectPublicKeyInfo"/> — this side's channel binding.</summary>
    public byte[] PublicKeyHash { get; }

    /// <summary>Generates one, for one connection.</summary>
    /// <param name="notBefore">Start of validity.</param>
    /// <param name="notAfter">End of validity.</param>
    /// <returns>The certificate and its key.</returns>
    /// <remarks>
    /// <para>
    /// The validity window is required by the encoding and read by nobody: 02
    /// §1 makes no trust decision about this certificate, so an expired one is
    /// no worse than a current one. The clock is a parameter rather than
    /// <see cref="DateTimeOffset.UtcNow"/> so that a test can pin it, not
    /// because anything downstream cares what it says.
    /// </para>
    /// <para>
    /// The certificate handed out is a PKCS#12 round trip of the one the
    /// request signed, on every platform, because two of them refuse the
    /// original: Schannel will not take a certificate whose private key is
    /// process-ephemeral as a server credential, and macOS requires the key
    /// in a keychain. The import gives the key to the OS for the certificate's
    /// lifetime and deletes it on dispose, which keeps 02 §1's "never
    /// persisted" in the sense that rule exists for — the key never outlives
    /// the connection. One unconditional path also means the suite exercises
    /// on Linux exactly what ships on the platforms that made it necessary.
    /// </para>
    /// </remarks>
    public static EphemeralTlsCertificate Create(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        try
        {
            // The subject name is a placeholder for a field that must exist and
            // that nothing reads. Putting a hostname or an identity here would
            // invite someone to check it, and there is nothing here worth
            // checking — the pinned key is checked in 02 §3.
            var request = new CertificateRequest(
                "CN=fallbackplan-peer-session", key, HashAlgorithmName.SHA256);

            using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);
            return new EphemeralTlsCertificate(
                key,
                X509CertificateLoader.LoadPkcs12(
                    ephemeral.Export(X509ContentType.Pfx), password: null));
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Reads the channel binding from a certificate a peer presented.</summary>
    /// <param name="certificate">The peer's certificate.</param>
    /// <returns>SHA-256 of its DER <c>SubjectPublicKeyInfo</c>.</returns>
    public static byte[] BindingOf(X509Certificate2 certificate)
    {
        ThrowHelper.ThrowIfNull(certificate);
        return SessionBinding.TlsPublicKeyHash(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Destroys the key, per 02 §1.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Certificate.Dispose();
        _key.Dispose();
    }
}
