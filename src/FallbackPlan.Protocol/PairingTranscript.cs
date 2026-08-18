using Bodu;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Protocol.Resources;

namespace FallbackPlan.Protocol;

/// <summary>One side's contribution to a pairing (specification peer-protocol 01 §2.2).</summary>
/// <param name="Identity">That side's long-lived peer identity.</param>
/// <param name="EphemeralPublicKey">Its X25519 share for this ceremony, 32 bytes.</param>
/// <param name="Nonce">Its fresh 32-byte nonce.</param>
public sealed record PairingContribution(
    PeerIdentity Identity,
    ReadOnlyMemory<byte> EphemeralPublicKey,
    ReadOnlyMemory<byte> Nonce);

/// <summary>
/// The bytes both sides of a pairing must agree on, and the short
/// authentication string derived from them (specification peer-protocol 01 §2.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything that identifies either side is inside the transcript.</b> Both
/// long-lived identities, both ephemeral shares, both nonces and the protocol
/// version — so an attacker relaying between two sessions holds two different
/// transcripts and cannot make the two strings agree. The comparison the humans
/// perform is what defeats the relay; this type exists to make sure the thing
/// they compare actually covers what matters.
/// </para>
/// <para>
/// A derivation that left out either identity would be the classic mistake: the
/// two humans would compare strings that matched while each was talking to the
/// attacker rather than to the other.
/// </para>
/// </remarks>
public static class PairingTranscript
{
    /// <summary>X25519 keys and shared secrets are 32 bytes.</summary>
    public const int ExchangeKeyLength = 32;

    /// <summary>Nonces are 32 bytes.</summary>
    public const int NonceLength = 32;

    /// <summary>The short authentication string is 30 bits — six base32 characters.</summary>
    public const int ShortAuthenticationStringCharacters = 6;

    private const string SasLabel = "fbp-peer-v1:sas";

    private const string ConfirmLabel = "fbp-peer-v1:confirm";

    /// <summary>Builds the transcript both sides derive from.</summary>
    /// <param name="offerer">The side that initiated.</param>
    /// <param name="responder">The side that accepted.</param>
    /// <param name="protocolVersion">The version selected in the accept.</param>
    /// <param name="offererPins">The role the offerer declared it will record for the responder (01 §2.2 key 7).</param>
    /// <param name="responderPins">The role the responder declared it will record for the offerer.</param>
    /// <returns>The transcript bytes.</returns>
    /// <remarks>
    /// The two declared roles are inside the transcript so that what each
    /// side records is part of what both humans approve and both keys sign —
    /// the grants cannot silently disagree about which of them lends the disk
    /// (ADR-0030 Amendment 2).
    /// </remarks>
    public static byte[] Build(
        PairingContribution offerer,
        PairingContribution responder,
        ushort protocolVersion,
        PeerRole offererPins,
        PeerRole responderPins)
    {
        ThrowHelper.ThrowIfNull(offerer);
        ThrowHelper.ThrowIfNull(responder);

        Validate(offerer, nameof(offerer));
        Validate(responder, nameof(responder));

        // Order is fixed by role, not by who spoke first on the wire, so both
        // sides build identical bytes without having to agree on anything else.
        var transcript = new byte[
            (PeerIdentity.KeyLength * 2) + (ExchangeKeyLength * 2) + (NonceLength * 2) + sizeof(ushort) + 2];
        var at = 0;

        offerer.Identity.PublicKey.CopyTo(transcript.AsSpan(at));
        at += PeerIdentity.KeyLength;
        responder.Identity.PublicKey.CopyTo(transcript.AsSpan(at));
        at += PeerIdentity.KeyLength;

        offerer.EphemeralPublicKey.Span.CopyTo(transcript.AsSpan(at));
        at += ExchangeKeyLength;
        responder.EphemeralPublicKey.Span.CopyTo(transcript.AsSpan(at));
        at += ExchangeKeyLength;

        offerer.Nonce.Span.CopyTo(transcript.AsSpan(at));
        at += NonceLength;
        responder.Nonce.Span.CopyTo(transcript.AsSpan(at));
        at += NonceLength;

        BinaryPrimitives.WriteUInt16BigEndian(transcript.AsSpan(at), protocolVersion);
        at += sizeof(ushort);

        transcript[at] = (byte)offererPins;
        transcript[at + 1] = (byte)responderPins;
        return transcript;
    }

    /// <summary>
    /// Derives the six-character string the two humans compare.
    /// </summary>
    /// <param name="sharedSecret">The X25519 shared secret, 32 bytes.</param>
    /// <param name="offererNonce">The offerer's nonce.</param>
    /// <param name="responderNonce">The responder's nonce.</param>
    /// <param name="transcript">The transcript from <see cref="Build"/>.</param>
    /// <returns>Six lowercase base32 characters.</returns>
    public static string ShortAuthenticationString(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> offererNonce,
        ReadOnlySpan<byte> responderNonce,
        ReadOnlySpan<byte> transcript)
    {
        if (sharedSecret.Length != ExchangeKeyLength)
        {
            throw new ArgumentException(Strings.FormatPairingTranscript_XSharedSecretBytesGot(ExchangeKeyLength, sharedSecret.Length), nameof(sharedSecret));
        }

        var salt = new byte[NonceLength * 2];
        offererNonce.CopyTo(salt);
        responderNonce.CopyTo(salt.AsSpan(NonceLength));

        var info = new byte[SasLabel.Length + transcript.Length];
        System.Text.Encoding.ASCII.GetBytes(SasLabel, info);
        transcript.CopyTo(info.AsSpan(SasLabel.Length));

        // 30 bits is four bytes with two spare, so derive four and mask. Base32
        // encodes five bits per character, and six characters is exactly 30.
        var derived = new byte[4];
        try
        {
            // The platform's HKDF, not the package's: ADR-0019 gate 1 forbids
            // reimplementing a primitive .NET already supplies.
            System.Security.Cryptography.HKDF.DeriveKey(
                HashAlgorithmName.SHA256, sharedSecret, derived, salt, info);
            var bits = BinaryPrimitives.ReadUInt32BigEndian(derived) >> 2;
            return Render(bits);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    /// <summary>The HKDF label for the invite MAC key (00 §4, 01 §2.7).</summary>
    private const string InviteKeyLabel = "fbp-peer-v1:invite-key";

    /// <summary>The offerer's invite-proof label; distinct from the responder's so neither proof replays as the other.</summary>
    private const string InviteOffererLabel = "fbp-peer-v1:invite:offerer";

    /// <summary>The responder's invite-proof label.</summary>
    private const string InviteResponderLabel = "fbp-peer-v1:invite:responder";

    /// <summary>Derives the invite MAC key from a code's secret (01 §2.7).</summary>
    /// <param name="secret">The code's secret half.</param>
    /// <param name="inviteId">The code's public identifier, bound in as salt.</param>
    /// <returns>The 32-byte key.</returns>
    public static byte[] DeriveInviteKey(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> inviteId)
    {
        var key = new byte[32];
        System.Security.Cryptography.HKDF.DeriveKey(
            HashAlgorithmName.SHA256, secret, key, inviteId,
            System.Text.Encoding.ASCII.GetBytes(InviteKeyLabel));
        return key;
    }

    /// <summary>
    /// The invite proof one side sends on its confirmation (01 §2.7): HMAC
    /// under the code-derived key over the whole transcript and the TLS
    /// channel bindings. The transcript carries both identities, both
    /// ephemerals, both nonces, the version and both declared roles; the
    /// bindings tie the proof to this connection — so a relay holds two
    /// different inputs and can satisfy neither side, exactly as it cannot
    /// make two humans' strings agree.
    /// </summary>
    /// <param name="inviteKey">The key from <see cref="DeriveInviteKey"/>.</param>
    /// <param name="offerer">Whether this proof is the offerer's.</param>
    /// <param name="transcript">The transcript from <see cref="Build"/>.</param>
    /// <param name="initiatorBinding">The TLS initiator's channel binding.</param>
    /// <param name="responderBinding">The TLS responder's channel binding.</param>
    /// <returns>The 32-byte proof.</returns>
    public static byte[] InviteMac(
        ReadOnlySpan<byte> inviteKey,
        bool offerer,
        ReadOnlySpan<byte> transcript,
        ReadOnlySpan<byte> initiatorBinding,
        ReadOnlySpan<byte> responderBinding)
    {
        using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, inviteKey);
        mac.AppendData(System.Text.Encoding.ASCII.GetBytes(offerer ? InviteOffererLabel : InviteResponderLabel));
        mac.AppendData(transcript);
        mac.AppendData(initiatorBinding);
        mac.AppendData(responderBinding);
        return mac.GetHashAndReset();
    }

    /// <summary>The bytes a side signs to confirm a pairing (01 §2.4).</summary>
    /// <param name="transcript">The transcript from <see cref="Build"/>.</param>
    /// <returns>The bytes to sign.</returns>
    /// <remarks>
    /// Labelled separately from the short authentication string so a signature
    /// can never be replayed as a derivation input or the reverse — the domain
    /// separation of specification peer-protocol 00 §4.
    /// </remarks>
    public static byte[] ConfirmationBytes(ReadOnlySpan<byte> transcript)
    {
        var bytes = new byte[ConfirmLabel.Length + transcript.Length];
        System.Text.Encoding.ASCII.GetBytes(ConfirmLabel, bytes);
        transcript.CopyTo(bytes.AsSpan(ConfirmLabel.Length));
        return bytes;
    }

    /// <summary>Renders 30 bits as six lowercase base32 characters.</summary>
    private static string Render(uint bits)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        return string.Create(ShortAuthenticationStringCharacters, bits, static (span, value) =>
        {
            for (var i = ShortAuthenticationStringCharacters - 1; i >= 0; i--)
            {
                span[i] = Alphabet[(int)(value & 0x1F)];
                value >>= 5;
            }
        });
    }

    private static void Validate(PairingContribution contribution, string parameter)
    {
        if (contribution.EphemeralPublicKey.Length != ExchangeKeyLength)
        {
            throw new ArgumentException(Strings.FormatPairingTranscript_EphemeralKeyBytesGot(ExchangeKeyLength, contribution.EphemeralPublicKey.Length), parameter);
        }

        if (contribution.Nonce.Length != NonceLength)
        {
            throw new ArgumentException(Strings.FormatPairingTranscript_PairingNonceBytesGot(NonceLength, contribution.Nonce.Length), parameter);
        }
    }
}

/// <summary>One side's ephemeral X25519 share for a single pairing.</summary>
/// <remarks>
/// Ephemeral by construction: generated per ceremony, never stored, and
/// discarded with this object. The long-lived identity does the authenticating
/// (<see cref="PeerKeypair"/>); this only establishes a secret to derive the
/// short authentication string from.
/// </remarks>
public sealed class PairingExchange : IDisposable
{
    private readonly X25519 _exchange;

    private PairingExchange(X25519 exchange)
    {
        _exchange = exchange;
        PublicKey = exchange.ExportPublicKey();
        Nonce = RandomNumberGenerator.GetBytes(PairingTranscript.NonceLength);
    }

    /// <summary>This side's ephemeral public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>This side's fresh nonce.</summary>
    public byte[] Nonce { get; }

    /// <summary>Starts a ceremony with a fresh share and nonce.</summary>
    /// <returns>The exchange; dispose to clear it.</returns>
    public static PairingExchange Start()
    {
        var exchange = X25519.Create();
        try
        {
            exchange.GenerateKey();
            return new PairingExchange(exchange);
        }
        catch
        {
            exchange.Dispose();
            throw;
        }
    }

    /// <summary>Derives the shared secret from the other side's share.</summary>
    /// <param name="peerPublicKey">The other side's ephemeral public key.</param>
    /// <returns>The 32-byte shared secret.</returns>
    /// <exception cref="ArgumentException">The key is the wrong length or a low-order point.</exception>
    public byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != PairingTranscript.ExchangeKeyLength)
        {
            throw new ArgumentException(Strings.FormatPairingTranscript_EphemeralKeyBytesGot(PairingTranscript.ExchangeKeyLength, peerPublicKey.Length),
                nameof(peerPublicKey));
        }

        // A low-order point forces the shared secret to a value the sender
        // knows in advance, which would let it fix the string both humans
        // compare. Refusing here is cheaper than reasoning about it later.
        if (X25519.IsLowOrderPoint(peerPublicKey))
        {
            throw new ArgumentException(Strings.PairingExchange_PeerOfferedLowOrderX, nameof(peerPublicKey));
        }

        return _exchange.DeriveSharedSecret(peerPublicKey);
    }

    /// <inheritdoc />
    public void Dispose() => _exchange.Dispose();
}
