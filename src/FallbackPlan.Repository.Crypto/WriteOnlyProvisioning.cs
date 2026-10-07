using System.Buffers.Binary;
using System.Security.Cryptography;
using Bodu;
using KdfParameters = FallbackPlan.Domain.Configuration.Argon2Parameters;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The sealed envelopes a service's ceremonies exchange (ADR-0042 §4,
/// ADR-0070): <b>provisioning</b> carries the write bundle plus the KDF salt
/// and parameters the descriptor must record, a <b>restore grant</b> carries
/// the derived scalar alone, a <b>claim root</b> carries the Argon2id
/// output a rebuilt machine proves a replica with, and an <b>access key</b>
/// carries the secret an S3-compatible destination's requests are signed with
/// (ADR-0091). Each is sealed end-to-end to the service's published recipient
/// key with its own associated-data purpose, so none can be replayed as
/// another — and the passphrase itself is in none of them.
/// </summary>
public static class WriteOnlyProvisioning
{
    /// <summary>The longest secret access key an envelope carries.</summary>
    /// <remarks>
    /// Several times what any provider issues. The bound exists so a pasted
    /// file is refused where it was pasted rather than stored and sent as a
    /// signing key on every request.
    /// </remarks>
    public const int MaximumAccessKeySecretLength = 256;

    private static ReadOnlySpan<byte> ProvisionMagic => "FBPPROV1"u8;

    private static ReadOnlySpan<byte> ProvisionAad => "fbp/provision/v2"u8;

    private static ReadOnlySpan<byte> GrantAad => "fbp/restore-grant/v2"u8;

    private static ReadOnlySpan<byte> AccessKeyPurpose => "fbp/destination-access-key/v1"u8;

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

    /// <summary>
    /// Seals the secret access key of an S3-compatible destination for the
    /// service's recipient key (ADR-0091), bound to the destination and the
    /// access key id it was typed for.
    /// </summary>
    /// <remarks>
    /// The destination name and key id are in the associated data, not the
    /// payload: they cross beside the envelope in clear, as the key id does
    /// on every request the store receives, and binding them means an
    /// envelope cannot be replayed into another destination's credentials or
    /// stored beside a key id it was not issued with.
    /// </remarks>
    /// <param name="recipientPublicKey">The service's published recipient key.</param>
    /// <param name="destinationName">The destination the key is for, by its declared name.</param>
    /// <param name="accessKeyId">The access key id the secret belongs to.</param>
    /// <param name="secretAccessKey">The secret, as the provider issued it.</param>
    /// <exception cref="ArgumentException">
    /// A name is blank, or the secret is empty, longer than
    /// <see cref="MaximumAccessKeySecretLength"/>, or carries a control character.
    /// </exception>
    public static byte[] SealAccessKeySecret(
        ReadOnlySpan<byte> recipientPublicKey, string destinationName, string accessKeyId, string secretAccessKey)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationName);
        ThrowHelper.ThrowIfNullOrWhiteSpace(accessKeyId);
        ThrowHelper.ThrowIfNull(secretAccessKey);

        if (!IsAccessKeySecret(secretAccessKey))
        {
            throw new ArgumentException(
                Resources.Strings.FormatWriteOnlyProvisioning_AccessKeySecretMalformed(MaximumAccessKeySecretLength),
                nameof(secretAccessKey));
        }

        var payload = System.Text.Encoding.UTF8.GetBytes(secretAccessKey);
        try
        {
            return ContentSealing.SealPayload(
                recipientPublicKey, payload, AccessKeyAssociatedData(destinationName, accessKeyId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Opens an access-key envelope with the service's recipient scalar.</summary>
    /// <param name="recipientPrivateKey">The service's recipient scalar.</param>
    /// <param name="sealedBytes">The envelope.</param>
    /// <param name="destinationName">The destination the envelope must have been sealed for.</param>
    /// <param name="accessKeyId">The access key id it must have been sealed beside.</param>
    /// <exception cref="SealedContentException">
    /// The envelope does not open — another recipient, another destination or
    /// key id, another purpose, tampered bytes — or what it carries is not a
    /// secret access key.
    /// </exception>
    public static string OpenAccessKeySecret(
        ReadOnlySpan<byte> recipientPrivateKey, ReadOnlySpan<byte> sealedBytes,
        string destinationName, string accessKeyId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationName);
        ThrowHelper.ThrowIfNullOrWhiteSpace(accessKeyId);

        var payload = ContentSealing.OpenPayload(
            recipientPrivateKey, sealedBytes, AccessKeyAssociatedData(destinationName, accessKeyId));
        try
        {
            string secret;
            try
            {
                secret = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
            }

            // One refusal shape for every envelope that is not this one,
            // as a provisioning envelope hiding garbage gets.
            return IsAccessKeySecret(secret)
                ? secret
                : throw new SealedContentException(Resources.Strings.ContentSealing_DoesNotOpen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static bool IsAccessKeySecret(string candidate) =>
        candidate.Length is > 0 and <= MaximumAccessKeySecretLength && !candidate.Any(char.IsControl);

    private static byte[] AccessKeyAssociatedData(string destinationName, string accessKeyId)
    {
        // Purpose, then each name with its length before it, so no two
        // (destination, key id) pairs share an encoding.
        var destination = System.Text.Encoding.UTF8.GetBytes(destinationName);
        var keyId = System.Text.Encoding.UTF8.GetBytes(accessKeyId);
        var data = new byte[AccessKeyPurpose.Length + 4 + destination.Length + 4 + keyId.Length];
        AccessKeyPurpose.CopyTo(data);
        var offset = AccessKeyPurpose.Length;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), destination.Length);
        destination.CopyTo(data, offset + 4);
        offset += 4 + destination.Length;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), keyId.Length);
        keyId.CopyTo(data, offset + 4);
        return data;
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
