using Bodu;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing.Resources;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Seals and opens standalone metadata records (ADR-0022 §Decision 1;
/// specification 07 §2, 08 §2, 06 §6): the record key derives exactly as a
/// blob's first record's would — <c>HKDF-Expand(metadata_key[g],
/// "fbp/blob/v1" ‖ salt ‖ writer ‖ u64(counter))</c> — and the nonce and
/// 55-byte AAD are specification 04's at ordinal 0. One auditable
/// construction covers blob records and standalone records alike; the
/// shared counter space guarantees the derivation domains never collide.
/// </summary>
public static class StandaloneRecordCipher
{
    /// <summary>
    /// Seals <paramref name="payload"/> into a complete standalone object:
    /// 72-byte prefix, 54-byte header at ordinal 0, ciphertext, tag. The
    /// payload is stored uncompressed — standalone metadata records are
    /// small CBOR bodies, and profile <c>none</c> keeps the object
    /// reconstructible from one derivation.
    /// </summary>
    /// <param name="repositoryId">The repository the AAD binds to.</param>
    /// <param name="metadataClassKey">The metadata key for <paramref name="keyGeneration"/>.</param>
    /// <param name="keyGeneration">The key generation recorded in the prefix.</param>
    /// <param name="writerId">The writer identity.</param>
    /// <param name="counter">The counter from the writer's shared sequence space (specification 08 §2).</param>
    /// <param name="objectType">The record's object type (0x08–0x0A, or 0x04 for a standalone snapshot).</param>
    /// <param name="objectId">The record's object identifier.</param>
    /// <param name="payload">The plaintext CBOR body.</param>
    /// <param name="blobSalt">32 CSPRNG bytes when omitted; injectable for deterministic fixtures.</param>
    public static byte[] Seal(
        RepositoryId repositoryId,
        ReadOnlySpan<byte> metadataClassKey,
        KeyGeneration keyGeneration,
        WriterId writerId,
        ulong counter,
        ObjectType objectType,
        ObjectId objectId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> blobSalt = default)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException(Strings.StandaloneRecordCipher_StandaloneRecordSPayloadNever, nameof(payload));
        }

        Span<byte> salt = stackalloc byte[StandaloneRecordFraming.BlobSaltLength];
        if (blobSalt.IsEmpty)
        {
            RandomNumberGenerator.Fill(salt);
        }
        else if (blobSalt.Length == StandaloneRecordFraming.BlobSaltLength)
        {
            blobSalt.CopyTo(salt);
        }
        else
        {
            throw new ArgumentException(Strings.FormatBlobWriter_BlobSaltExactlyBytes(StandaloneRecordFraming.BlobSaltLength), nameof(blobSalt));
        }

        var header = new RecordHeader(
            objectType,
            CompressionProfile.None,
            EncryptionProfile.Aes256GcmV1,
            ordinal: 0,
            (ulong)payload.Length,
            (uint)payload.Length,
            objectId);

        var buffer = new byte[StandaloneRecordFraming.PrefixLength + RecordHeader.Length + payload.Length + RecordCipher.TagLength];
        var span = buffer.AsSpan();

        StandaloneRecordFraming.WritePrefix(
            FormatLimits.FormatVersion, keyGeneration, salt, writerId, counter, span);
        header.WriteTo(span.Slice(StandaloneRecordFraming.PrefixLength, RecordHeader.Length));

        var recordKey = new byte[BlobKeyDeriver.BlobKeyLength];

        try
        {
            BlobKeyDeriver.Derive(metadataClassKey, salt, writerId, counter, recordKey);

            Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
            RecordNonce.Write(0, nonce);
            Span<byte> aad = stackalloc byte[RecordAad.Length];
            RecordAad.Write(repositoryId, FormatLimits.FormatVersion, objectType, objectId, 0, aad);

            RecordCipher.Seal(
                recordKey,
                nonce,
                aad,
                payload,
                span.Slice(StandaloneRecordFraming.PrefixLength + RecordHeader.Length, payload.Length),
                span.Slice(StandaloneRecordFraming.PrefixLength + RecordHeader.Length + payload.Length, RecordCipher.TagLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recordKey);
        }

        return buffer;
    }

    /// <summary>
    /// Authenticates and decrypts a parsed standalone record. Returns
    /// <see langword="false"/> with nothing emitted on any authentication
    /// failure — a record moved between repositories, a tampered byte, a
    /// wrong-generation key (specification 04 §7).
    /// </summary>
    public static bool TryOpen(
        StandaloneRecord record,
        RepositoryId repositoryId,
        ReadOnlySpan<byte> metadataClassKey,
        out byte[] plaintext)
    {
        ThrowHelper.ThrowIfNull(record);

        plaintext = [];

        if (record.Header.CompressionProfile != CompressionProfile.None ||
            record.Header.EncryptionProfile != EncryptionProfile.Aes256GcmV1)
        {
            return false;
        }

        var recordKey = new byte[BlobKeyDeriver.BlobKeyLength];
        var output = new byte[record.Header.StoredLength];

        try
        {
            BlobKeyDeriver.Derive(metadataClassKey, record.BlobSalt.Span, record.WriterId, record.Counter, recordKey);

            Span<byte> nonce = stackalloc byte[RecordNonce.AesGcmLength];
            RecordNonce.Write(0, nonce);
            Span<byte> aad = stackalloc byte[RecordAad.Length];
            RecordAad.Write(repositoryId, record.FormatVersion, record.Header.ObjectType, record.Header.ObjectId, 0, aad);

            if (!RecordCipher.TryOpen(recordKey, nonce, aad, record.Ciphertext.Span, record.Tag.Span, output))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recordKey);
        }

        plaintext = output;
        return true;
    }
}
