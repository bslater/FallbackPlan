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

    /// <summary>
    /// The longest prefix any container puts in front of a record's
    /// ciphertext — a format-3 data record's carried nonce and sealed key.
    /// A caller that must size a read <em>before</em> it has the blob's
    /// envelope (a prefetch, which is what folds the envelope into the
    /// first run) uses this and over-fetches by at most 92 bytes per record
    /// rather than paying a read to learn the exact figure.
    /// </summary>
    public const int MaxPrefixLength = RecordNonce.AesGcmLength + SealedRecordKey.SealedLength;

    /// <summary>
    /// The largest a record of this stored length can be framed as, in any
    /// container this reader accepts — the bound that lets a range be sized
    /// without the envelope.
    /// </summary>
    public static long MaxRecordLength(uint storedLength) =>
        RecordHeader.Length + MaxPrefixLength + storedLength + Crypto.RecordCipher.TagLength;

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
