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

    /// <summary>Everything in the provisioning payload except the credential: magic ‖ … ‖ salt ‖ memory ‖ iterations ‖ parallelism.</summary>
    /// <remarks>
    /// The credential's own length is asked of the credential
    /// (<see cref="RepositoryWriteCredential.LengthOf"/>) rather than assumed,
    /// because the two ends of this envelope are not always the same build: a
    /// console seals it and a service opens it, and an envelope carrying the
    /// shape an older console writes must not be refused as if it were
    /// tampered with.
    /// </remarks>
    private const int ProvisionFramingLength = 8 + KekDerivation.SaltLength + 4 + 4 + 1;

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

        var credential = authority.Credential.ToBytes();
        var payload = new byte[ProvisionFramingLength + credential.Length];
        try
        {
            ProvisionMagic.CopyTo(payload);
            credential.CopyTo(payload, 8);
            var offset = 8 + credential.Length;
            kdfSalt.CopyTo(payload.AsSpan(offset));
            offset += KekDerivation.SaltLength;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), kdfParameters.MemoryKiB);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset + 4), kdfParameters.Iterations);
            payload[offset + 8] = kdfParameters.Parallelism;

            return ContentSealing.SealPayload(recipientPublicKey, payload, ProvisionAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
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
            var credentialLength = payload.Length > 8 && payload.AsSpan(0, 8).SequenceEqual(ProvisionMagic)
                ? RepositoryWriteCredential.LengthOf(payload.AsSpan(8))
                : -1;
            if (credentialLength < 0 || payload.Length != ProvisionFramingLength + credentialLength)
            {
                throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
            }

            RepositoryWriteCredential credential;
            try
            {
                credential = RepositoryWriteCredential.FromBytes(payload.AsSpan(8, credentialLength));
            }
            catch (ArgumentException)
            {
                // A well-sealed envelope whose embedded credential is not one
                // is refused exactly as a tampered envelope is — the caller
                // gets one refusal shape, never a leaked parse detail.
                throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
            }
            var offset = 8 + credentialLength;
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

    /// <summary>
    /// Seals a <b>reclaim</b> grant — the derived reclaim sub-root — for the
    /// service's recipient key
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §6).
    /// </summary>
    /// <remarks>
    /// Named separately from <see cref="SealGrant"/> so a caller cannot send
    /// the power to read where the power to delete is meant, or the reverse.
    /// The envelopes are indistinguishable on the wire — both are opaque
    /// 32-byte payloads under the same AAD — which is precisely why the
    /// service proves a reclaim grant against a tombstone the repository
    /// already holds before letting it author anything: a sealing scalar
    /// arriving in a reclaim grant's place will not verify one.
    /// </remarks>
    /// <param name="recipientPublicKey">The service's published recipient key.</param>
    /// <param name="reclaimRoot">The 32-byte reclaim sub-root.</param>
    /// <exception cref="ArgumentException">The sub-root is not exactly 32 bytes.</exception>
    public static byte[] SealReclaimGrant(
        ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> reclaimRoot)
    {
        if (reclaimRoot.Length != WriteOnlyDerivation.ReclaimKeyLength)
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyDerivation_ReclaimSeedExactlyBytes(
                    WriteOnlyDerivation.ReclaimKeyLength),
                nameof(reclaimRoot));
        }

        return ContentSealing.SealPayload(recipientPublicKey, reclaimRoot, GrantAad);
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
