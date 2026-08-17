using System.Security.Cryptography;
using Bodu.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Protocol.Resources;

namespace FallbackPlan.Protocol;

/// <summary>
/// A peer's identity: an Ed25519 public key, and nothing else
/// (specification peer-protocol 01 §1; ADR-0030 §1).
/// </summary>
/// <remarks>
/// <para>
/// A peer is never identified by a name, an address, or a <c>device_id</c> —
/// those are labels shown beside the key and carry no authority. The key is the
/// identity, which is why this type has no other field.
/// </para>
/// <para>
/// It is unrelated to the repository. The signing key of
/// ADR-0020 is repository-scoped and
/// derived from the master key; authenticating a peer with it would mean giving
/// the master key to the destination whose disk is being borrowed. A destination
/// holds one of these and no repository key at all.
/// </para>
/// </remarks>
public sealed record PeerIdentity
{
    /// <summary>Ed25519 public keys are 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>The fingerprint is the first 16 bytes of the key's SHA-256.</summary>
    public const int FingerprintLength = 16;

    private readonly byte[] _publicKey;

    private PeerIdentity(byte[] publicKey) => _publicKey = publicKey;

    /// <summary>The public key — the identity itself.</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>
    /// The base32 fingerprint, for display and for indexing grants.
    /// </summary>
    /// <remarks>
    /// A convenience only. Verification is always against the full key
    /// (01 §1): a fingerprint comparison is a shorter comparison, and shorter
    /// is exactly what an attacker searching for a collision wants.
    /// </remarks>
    public string Fingerprint => Base32.Encode(SHA256.HashData(_publicKey).AsSpan(0, FingerprintLength).ToArray());

    /// <summary>Wraps a public key as an identity.</summary>
    /// <param name="publicKey">The 32-byte Ed25519 public key.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    public static PeerIdentity FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.FormatPeerIdentity_PeerIdentityBytesGot(KeyLength, publicKey.Length), nameof(publicKey));
        }

        return new PeerIdentity(publicKey.ToArray());
    }

    /// <summary>Verifies a signature made by this peer.</summary>
    /// <param name="data">What was signed.</param>
    /// <param name="signature">The 64-byte signature.</param>
    /// <returns><see langword="true"/> when it verifies.</returns>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != PeerKeypair.SignatureLength)
        {
            return false;
        }

        using var key = Ed25519.Create();
        key.ImportPublicKey(_publicKey);
        return key.VerifyData(data, signature);
    }

    /// <inheritdoc />
    public bool Equals(PeerIdentity? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(_publicKey, other._publicKey);

    /// <inheritdoc />
    public override int GetHashCode() => BitConverter.ToInt32(_publicKey, 0);

    /// <inheritdoc />
    public override string ToString() => Fingerprint;
}
