using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Records;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// What sits between a record's 54-byte header and its ciphertext, by the
/// container's format version and class (specification 04 §2): nothing in a
/// format-2 container; a 12-byte carried nonce in every format-3 record; and,
/// in a format-3 data record of the sealed plane, the 80-byte sealed record
/// key after it. One name for the one arithmetic every length site does, so
/// that a reader sizes its range read, a footer bounds its table, and a
/// writer bounds its blob from the same answer.
/// </summary>
public static class RecordFraming
{
    /// <summary>The carried nonce's offset within the prefix: first.</summary>
    public const int NonceOffset = 0;

    /// <summary>The sealed record key's offset within the prefix: after the nonce.</summary>
    public const int SealedKeyOffset = RecordNonce.AesGcmLength;

    /// <summary>The prefix length for a container of this version and class.</summary>
    public static int PrefixLength(ushort formatVersion, BlobClass blobClass)
    {
        if (!FormatVersions.HasRelocatableRecords(formatVersion))
        {
            return 0;
        }

        return FormatVersions.SealsContentPerRecord(formatVersion, blobClass == BlobClass.Data)
            ? RecordNonce.AesGcmLength + SealedRecordKey.SealedLength
            : RecordNonce.AesGcmLength;
    }

    /// <summary>A record's whole size in a container of this version and class: header, prefix, ciphertext, tag.</summary>
    public static long RecordLength(ushort formatVersion, BlobClass blobClass, uint storedLength) =>
        RecordHeader.Length + PrefixLength(formatVersion, blobClass) + storedLength + Crypto.RecordCipher.TagLength;
}
