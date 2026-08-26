using System.Buffers.Binary;
using System.Security.Cryptography;
using Bodu;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The sealed envelopes a service's ceremonies exchange (ADR-0042 §4,
/// ADR-0046): <b>provisioning</b> carries the write bundle plus the KDF salt
/// and parameters the descriptor must record, a <b>restore grant</b> carries
/// the derived scalar alone, and a <b>claim root</b> carries the Argon2id
/// output a rebuilt machine proves a replica with. Each is sealed end-to-end
/// to the service's published recipient key with its own associated-data
/// purpose, so none can be replayed as another — and the passphrase itself is
/// in none of them.
/// </summary>
public static class WriteOnlyProvisioning
{
    private static ReadOnlySpan<byte> ProvisionMagic => "FBPPROV1"u8;

    private static ReadOnlySpan<byte> ProvisionAad => "fbp/provision/v2"u8;

    private static ReadOnlySpan<byte> GrantAad => "fbp/restore-grant/v2"u8;

    /// <summary>
    /// The claim root's purpose (ADR-0046). Its own, and not the grant's: a
    /// restore grant reads one repository's content, while a claim root can
    /// re-point that repository's attribution at a new device on somebody
    /// else's disk. Sharing an AAD would let either envelope be replayed as
    /// the other, which is the whole reason each carries a purpose at all.
    /// </summary>
    private static ReadOnlySpan<byte> ClaimRootAad => "fbp/claim-root/v1"u8;

    /// <summary>The provisioning payload: magic ‖ credential ‖ salt ‖ memory ‖ iterations ‖ parallelism.</summary>
    private const int ProvisionPayloadLength = 8 + RepositoryWriteCredential.SerializedLength + KekDerivation.SaltLength + 4 + 4 + 1;

    /// <summary>Seals a provisioning envelope for the service's recipient key.</summary>
    public static byte[] SealProvision(
        ReadOnlySpan<byte> recipientPublicKey,
        RepositoryReadAuthority authority,
        ReadOnlySpan<byte> kdfSalt,
        KdfParameters kdfParameters)
    {
        ThrowHelper.ThrowIfNull(authority);
        ThrowHelper.ThrowIfNull(kdfParameters);

        if (kdfSalt.Length != KekDerivation.SaltLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatKekDerivation_KDFSaltExactlyBytesGot(KekDerivation.SaltLength, kdfSalt.Length),
                nameof(kdfSalt));
        }

        var payload = new byte[ProvisionPayloadLength];
        try
        {
            ProvisionMagic.CopyTo(payload);
            var credential = authority.Credential.ToBytes();
            credential.CopyTo(payload, 8);
            CryptographicOperations.ZeroMemory(credential);
            var offset = 8 + RepositoryWriteCredential.SerializedLength;
            kdfSalt.CopyTo(payload.AsSpan(offset));
            offset += KekDerivation.SaltLength;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), kdfParameters.MemoryKiB);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset + 4), kdfParameters.Iterations);
            payload[offset + 8] = kdfParameters.Parallelism;

            return ContentSealing.SealPayload(recipientPublicKey, payload, ProvisionAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Opens a provisioning envelope with the service's recipient scalar.</summary>
    /// <exception cref="SealedContentException">The envelope does not open, or its payload is not a provisioning payload.</exception>
    public static (RepositoryWriteCredential Credential, byte[] KdfSalt, KdfParameters KdfParameters) OpenProvision(
        ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes)
    {
        var payload = ContentSealing.OpenPayload(recipientPrivateKey, sealedBytes, ProvisionAad);
        try
        {
            if (payload.Length != ProvisionPayloadLength || !payload.AsSpan(0, 8).SequenceEqual(ProvisionMagic))
            {
                throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
            }

            RepositoryWriteCredential credential;
            try
            {
                credential = RepositoryWriteCredential.FromBytes(
                    payload.AsSpan(8, RepositoryWriteCredential.SerializedLength));
            }
            catch (ArgumentException)
            {
                // A well-sealed envelope whose embedded credential is not one
                // is refused exactly as a tampered envelope is — the caller
                // gets one refusal shape, never a leaked parse detail.
                throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
            }
            var offset = 8 + RepositoryWriteCredential.SerializedLength;
            var salt = payload.AsSpan(offset, KekDerivation.SaltLength).ToArray();
            offset += KekDerivation.SaltLength;
            var parameters = new KdfParameters
            {
                MemoryKiB = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset)),
                Iterations = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset + 4)),
                Parallelism = payload[offset + 8],
            };

            return (credential, salt, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Seals a restore grant — the derived scalar — for the service's recipient key.</summary>
    public static byte[] SealGrant(ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> sealingPrivateKey)
    {
        if (sealingPrivateKey.Length != WriteOnlyDerivation.SealingKeyLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatContentSealing_KeyExactlyBytes(WriteOnlyDerivation.SealingKeyLength),
                nameof(sealingPrivateKey));
        }

        return ContentSealing.SealPayload(recipientPublicKey, sealingPrivateKey, GrantAad);
    }

    /// <summary>Opens a restore grant with the service's recipient scalar.</summary>
    /// <exception cref="SealedContentException">The envelope does not open or is not a grant.</exception>
    public static byte[] OpenGrant(ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes)
    {
        var scalar = ContentSealing.OpenPayload(recipientPrivateKey, sealedBytes, GrantAad);
        if (scalar.Length != WriteOnlyDerivation.SealingKeyLength)
        {
            CryptographicOperations.ZeroMemory(scalar);
            throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
        }

        return scalar;
    }

    /// <summary>
    /// Seals a claim root — the Argon2id output the passphrase produces — for
    /// the service's recipient key (ADR-0046; peer-protocol 07 §5.2).
    /// </summary>
    /// <remarks>
    /// The client derives this from the passphrase and the recovery kit's KDF
    /// salt and parameters, because a rebuilt machine has the kit and no
    /// repository to read the salt from. What crosses the contract is this
    /// envelope; the passphrase stays where it was typed, exactly as in the
    /// two ceremonies above.
    /// </remarks>
    /// <param name="recipientPublicKey">The service's published recipient key.</param>
    /// <param name="claimRoot">The 32-byte Argon2id root.</param>
    /// <exception cref="ArgumentException"><paramref name="claimRoot"/> is not exactly 32 bytes.</exception>
    public static byte[] SealClaimRoot(ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> claimRoot)
    {
        if (claimRoot.Length != KekDerivation.KekLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatContentSealing_KeyExactlyBytes(KekDerivation.KekLength),
                nameof(claimRoot));
        }

        return ContentSealing.SealPayload(recipientPublicKey, claimRoot, ClaimRootAad);
    }

    /// <summary>Opens a claim root with the service's recipient scalar.</summary>
    /// <param name="recipientPrivateKey">The service's recipient scalar.</param>
    /// <param name="sealedBytes">The envelope.</param>
    /// <returns>The root. The caller owns it and must zero it.</returns>
    /// <exception cref="SealedContentException">The envelope does not open, or is not a claim root.</exception>
    public static byte[] OpenClaimRoot(ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes)
    {
        var root = ContentSealing.OpenPayload(recipientPrivateKey, sealedBytes, ClaimRootAad);
        if (root.Length != KekDerivation.KekLength)
        {
            CryptographicOperations.ZeroMemory(root);
            throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
        }

        return root;
    }
}
