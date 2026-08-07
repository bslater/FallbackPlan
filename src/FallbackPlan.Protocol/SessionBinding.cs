using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>Which end of the connection a peer is (specification peer-protocol 02 §3).</summary>
public enum PeerSessionRole
{
    /// <summary>The side that opened the connection.</summary>
    Initiator = 0,

    /// <summary>The side that accepted it.</summary>
    Responder = 1,
}

/// <summary>
/// One side's contribution to the authentication transcript
/// (specification peer-protocol 02 §3.2).
/// </summary>
/// <param name="Identity">That side's permanent peer identity.</param>
/// <param name="TlsPublicKeyHash">
/// SHA-256 over the DER <c>SubjectPublicKeyInfo</c> of that side's TLS
/// certificate — the channel binding.
/// </param>
/// <param name="Nonce">Fresh random bytes for this connection.</param>
public sealed record SessionBindingContribution(
    PeerIdentity Identity,
    ReadOnlyMemory<byte> TlsPublicKeyHash,
    ReadOnlyMemory<byte> Nonce);

/// <summary>
/// Binds a permanent peer identity to the TLS connection it is speaking over
/// (specification peer-protocol 02 §3).
/// </summary>
/// <remarks>
/// <para>
/// TLS here authenticates nobody: both sides present a self-signed certificate
/// generated for the connection and neither makes a trust decision about it
/// (02 §1). What makes the channel trustworthy is this — each side signs, with
/// the Ed25519 key the other has pinned, a transcript containing the hash of
/// <b>both</b> TLS public keys.
/// </para>
/// <para>
/// A man in the middle terminates TLS with certificates of its own, so the
/// hashes in the transcript it must produce are not the ones either genuine
/// peer signed. It cannot relay a captured proof, because that proof covers a
/// different byte string, and it cannot make the right one, because that needs
/// a private key it does not have. This is the guarantee RFC 7250 was chosen
/// for, obtained without it — see ADR-0030 Amendment 1.
/// </para>
/// </remarks>
public static class SessionBinding
{
    /// <summary>The version of this construction — not the negotiated protocol version (02 §3.2).</summary>
    public const ushort BindingVersion = 1;

    /// <summary>Nonce length, matching the pairing ceremony's.</summary>
    public const int NonceLength = 32;

    /// <summary>Length of a channel-binding hash.</summary>
    public const int TlsPublicKeyHashLength = 32;

    private const string InitiatorLabel = "fbp-peer-v1:tls-binding:initiator";
    private const string ResponderLabel = "fbp-peer-v1:tls-binding:responder";

    /// <summary>Generates a nonce for this connection.</summary>
    /// <returns>Fresh random bytes.</returns>
    public static byte[] Nonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>Hashes a DER <c>SubjectPublicKeyInfo</c> for use as a channel binding.</summary>
    /// <param name="subjectPublicKeyInfo">The DER-encoded SPKI of a TLS certificate.</param>
    /// <returns>The 32-byte hash.</returns>
    public static byte[] TlsPublicKeyHash(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        SHA256.HashData(subjectPublicKeyInfo);

    /// <summary>Builds the bytes a given role signs (02 §3.2).</summary>
    /// <param name="initiator">The contribution of the side that opened the connection.</param>
    /// <param name="responder">The contribution of the side that accepted it.</param>
    /// <param name="role">Whose signature this is for.</param>
    /// <returns>The label-prefixed, fixed-length transcript.</returns>
    /// <remarks>
    /// The two roles sign under different labels. Without that, an attacker
    /// could open a connection back to a peer and reflect the peer's own proof
    /// at it — the peer would verify its own signature and be satisfied.
    /// </remarks>
    public static byte[] Transcript(
        SessionBindingContribution initiator, SessionBindingContribution responder, PeerSessionRole role)
    {
        Validate(initiator, nameof(initiator));
        Validate(responder, nameof(responder));

        var label = role == PeerSessionRole.Initiator ? InitiatorLabel : ResponderLabel;
        var length = label.Length
            + sizeof(ushort)
            + (PeerIdentity.KeyLength * 2)
            + (TlsPublicKeyHashLength * 2)
            + (NonceLength * 2);

        var transcript = new byte[length];
        var at = 0;

        Encoding.ASCII.GetBytes(label, transcript);
        at += label.Length;

        BinaryPrimitives.WriteUInt16BigEndian(transcript.AsSpan(at), BindingVersion);
        at += sizeof(ushort);

        // Fixed-length fields in a fixed order, so there is no separator to get
        // wrong and no pair of different inputs that produce one transcript.
        initiator.Identity.PublicKey.CopyTo(transcript.AsSpan(at));
        at += PeerIdentity.KeyLength;
        responder.Identity.PublicKey.CopyTo(transcript.AsSpan(at));
        at += PeerIdentity.KeyLength;

        initiator.TlsPublicKeyHash.Span.CopyTo(transcript.AsSpan(at));
        at += TlsPublicKeyHashLength;
        responder.TlsPublicKeyHash.Span.CopyTo(transcript.AsSpan(at));
        at += TlsPublicKeyHashLength;

        initiator.Nonce.Span.CopyTo(transcript.AsSpan(at));
        at += NonceLength;
        responder.Nonce.Span.CopyTo(transcript.AsSpan(at));

        return transcript;
    }

    /// <summary>Signs the transcript for this side's role.</summary>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="initiator">The initiator's contribution.</param>
    /// <param name="responder">The responder's contribution.</param>
    /// <param name="role">This side's role.</param>
    /// <returns>The proof to send.</returns>
    public static SessionAuthProof Prove(
        PeerKeypair keypair,
        SessionBindingContribution initiator,
        SessionBindingContribution responder,
        PeerSessionRole role)
    {
        ArgumentNullException.ThrowIfNull(keypair);
        return new SessionAuthProof(keypair.Sign(Transcript(initiator, responder, role)));
    }

    /// <summary>Checks a peer's proof, and says nothing about which check failed.</summary>
    /// <param name="proof">What the peer sent.</param>
    /// <param name="initiator">The initiator's contribution.</param>
    /// <param name="responder">The responder's contribution.</param>
    /// <param name="peerRole">The peer's role — the opposite of this side's.</param>
    /// <returns><see langword="true"/> when the peer proved possession.</returns>
    /// <remarks>
    /// Verified against the transcript <b>this</b> side built from the
    /// certificates actually exchanged here, which is the whole mechanism: a
    /// proof made over some other connection's certificates does not verify
    /// against this one's.
    /// </remarks>
    public static bool Verify(
        SessionAuthProof proof,
        SessionBindingContribution initiator,
        SessionBindingContribution responder,
        PeerSessionRole peerRole)
    {
        ArgumentNullException.ThrowIfNull(proof);
        Validate(initiator, nameof(initiator));
        Validate(responder, nameof(responder));

        var identity = peerRole == PeerSessionRole.Initiator ? initiator.Identity : responder.Identity;
        return identity.Verify(Transcript(initiator, responder, peerRole), proof.Signature.Span);
    }

    private static void Validate(SessionBindingContribution contribution, string parameter)
    {
        ArgumentNullException.ThrowIfNull(contribution, parameter);

        if (contribution.TlsPublicKeyHash.Length != TlsPublicKeyHashLength)
        {
            throw new ArgumentException(
                $"A channel binding is {TlsPublicKeyHashLength} bytes; got {contribution.TlsPublicKeyHash.Length}.",
                parameter);
        }

        if (contribution.Nonce.Length != NonceLength)
        {
            throw new ArgumentException(
                $"A binding nonce is {NonceLength} bytes; got {contribution.Nonce.Length}.", parameter);
        }
    }
}
