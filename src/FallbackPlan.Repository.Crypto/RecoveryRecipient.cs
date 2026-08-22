using System.Security.Cryptography;
using FallbackPlan.Repository.Crypto.Resources;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The recipient a set-configuration object is sealed to (specification 11
/// §5.1, 03 §4; ADR-0047): <c>HKDF-Expand(master_key, "fbp/recovery/v1", 32)</c>
/// read as an X25519 scalar, so a v1 writer holds both halves and a machine
/// rebuilt from nothing reproduces them from the passphrase alone.
/// </summary>
/// <remarks>
/// <para>
/// The second seal is the point. A set-configuration record's outer framing
/// is sealed under the metadata key like any standalone record, so a writer
/// can locate and replace these objects while running — but a format-v2
/// service is granted the whole structure plane by design (ADR-0042), and an
/// unsealed configuration would hand a compromised write-only hub the user's
/// folder layout, schedule and rules. Sealed here, the hub writes an envelope
/// it can never open.
/// </para>
/// <para>
/// A **format-v2 repository derives nothing from this type**: it already has
/// an X25519 recipient in its descriptor (<c>fbp/seal/v2</c>) and seals to
/// that one, so a single construction serves both formats and there is no
/// second key to distribute. <see cref="Seal"/> and <see cref="Open"/> take
/// the recipient as a parameter for exactly that reason.
/// </para>
/// <para>
/// This key is ungenerational, unlike the signing key. What it protects is
/// not per-snapshot state — a repository has one recovery recipient for its
/// life — and a recovering device must derive it from the passphrase without
/// first having to learn which generation to ask for.
/// </para>
/// </remarks>
public static class RecoveryRecipient
{
    /// <summary>The X25519 scalar and public key length: 32 bytes.</summary>
    public const int KeyLength = 32;

    private static ReadOnlySpan<byte> Label => "fbp/recovery/v1"u8;

    /// <summary>
    /// The associated data every configuration envelope carries. It is what
    /// stops one opening as a provisioning or restore-grant envelope, which
    /// share the construction and differ only here.
    /// </summary>
    private static ReadOnlySpan<byte> ConfigurationAad => "fbp/config/v1"u8;

    /// <summary>
    /// Derives a format-v1 repository's recovery scalar from its master key.
    /// The caller owns the returned bytes and should zero them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="masterKey"/> is not exactly 32 bytes.</exception>
    public static byte[] DeriveScalar(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != KeyLength)
        {
            throw new ArgumentException(Strings.RecoveryRecipient_MasterKeyExactlyBytes, nameof(masterKey));
        }

        var scalar = new byte[KeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, scalar, Label);
        return scalar;
    }

    /// <summary>Computes the public half of a recovery scalar.</summary>
    /// <exception cref="ArgumentException"><paramref name="scalar"/> is not exactly 32 bytes.</exception>
    public static byte[] PublicKeyOf(ReadOnlySpan<byte> scalar) => ContentSealing.PublicKeyOf(scalar);

    /// <summary>
    /// Seals a configuration payload to <paramref name="recipientPublicKey"/> —
    /// a v1 repository's recovery public key, or a v2 repository's descriptor
    /// sealing public key.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not 32 bytes, or is a low-order point.</exception>
    public static byte[] Seal(ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> configuration) =>
        ContentSealing.SealPayload(recipientPublicKey, configuration, ConfigurationAad);

    /// <summary>Opens an envelope sealed by <see cref="Seal"/>.</summary>
    /// <exception cref="ArgumentException">A length is impossible, or the ephemeral share is a low-order point.</exception>
    /// <exception cref="SealedContentException">
    /// The envelope does not open: the wrong recipient, a different purpose, or tampered bytes.
    /// </exception>
    public static byte[] Open(ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes) =>
        ContentSealing.OpenPayload(recipientPrivateKey, sealedBytes, ConfigurationAad);
}
