using System.Buffers.Binary;
using System.Security.Cryptography;
using Bodu;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The two sealed envelopes a write-only repository's ceremonies exchange
/// (ADR-0042 §4): <b>provisioning</b> carries the write bundle plus the KDF
/// salt and parameters the descriptor must record, and a <b>restore grant</b>
/// carries the derived scalar alone. Each is sealed end-to-end to the
/// service's published recipient key with its own associated-data purpose,
/// so one can never be replayed as the other — and the passphrase itself is
/// in neither.
/// </summary>
public static class WriteOnlyProvisioning
{
    private static ReadOnlySpan<byte> ProvisionMagic => "FBPPROV1"u8;

    private static ReadOnlySpan<byte> ProvisionAad => "fbp/provision/v2"u8;

    private static ReadOnlySpan<byte> GrantAad => "fbp/restore-grant/v2"u8;

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
}
