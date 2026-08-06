using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// A parsed standalone metadata record (ADR-0022 §Decision 1): the cleartext
/// key-derivation selectors, the verbatim 04 §2 record header at ordinal 0,
/// and the ciphertext and tag — positions validated, nothing decrypted.
/// </summary>
public sealed class StandaloneRecord
{
    internal StandaloneRecord(
        ushort formatVersion,
        KeyGeneration keyGeneration,
        ReadOnlyMemory<byte> blobSalt,
        WriterId writerId,
        ulong counter,
        RecordHeader header,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag)
    {
        FormatVersion = formatVersion;
        KeyGeneration = keyGeneration;
        BlobSalt = blobSalt;
        WriterId = writerId;
        Counter = counter;
        Header = header;
        Ciphertext = ciphertext;
        Tag = tag;
    }

    /// <summary>The format version from the cleartext prefix.</summary>
    public ushort FormatVersion { get; }

    /// <summary>The key generation — selects the metadata key to derive from.</summary>
    public KeyGeneration KeyGeneration { get; }

    /// <summary>The 32-byte blob salt.</summary>
    public ReadOnlyMemory<byte> BlobSalt { get; }

    /// <summary>The writer identity.</summary>
    public WriterId WriterId { get; }

    /// <summary>The counter from the writer's shared sequence space (specification 08 §2).</summary>
    public ulong Counter { get; }

    /// <summary>The 04 §2 record header, ordinal 0.</summary>
    public RecordHeader Header { get; }

    /// <summary>The record ciphertext.</summary>
    public ReadOnlyMemory<byte> Ciphertext { get; }

    /// <summary>The 16-byte authentication tag.</summary>
    public ReadOnlyMemory<byte> Tag { get; }
}

/// <summary>
/// The <c>FBPKSREC</c> framing for metadata records stored as standalone
/// objects — index deltas, checkpoints, journal records, and the standalone
/// snapshot copy (ADR-0022 §Decision 1; specification 07 §2, 08 §2, 06 §6).
/// A 72-byte cleartext prefix carries exactly the selectors the blob
/// envelope would have carried — key generation, blob salt, writer, counter
/// — so specification 04's key derivation, nonce, and AAD apply
/// byte-for-byte at ordinal 0. Cryptographically a standalone record is a
/// one-record blob without the blob.
/// </summary>
public static class StandaloneRecordFraming
{
    /// <summary>The cleartext prefix length: magic through counter.</summary>
    public const int PrefixLength = 72;

    /// <summary>The framing magic, <c>"FBPKSREC"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKSREC"u8;

    /// <summary>The salt length — identical to the blob envelope's.</summary>
    public const int BlobSaltLength = 32;

    /// <summary>
    /// Writes the 72-byte cleartext prefix into
    /// <paramref name="destination"/>.
    /// </summary>
    public static void WritePrefix(
        ushort formatVersion,
        KeyGeneration keyGeneration,
        ReadOnlySpan<byte> blobSalt,
        WriterId writerId,
        ulong counter,
        Span<byte> destination)
    {
        if (blobSalt.Length != BlobSaltLength)
        {
            throw new ArgumentException($"The blob salt is exactly {BlobSaltLength} bytes.", nameof(blobSalt));
        }

        if (destination.Length < PrefixLength)
        {
            throw new ArgumentException($"The destination must hold {PrefixLength} bytes.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], formatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], 0); // reserved, MUST be zero (00 §9)
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], keyGeneration.Value);
        blobSalt.CopyTo(destination[16..]);
        writerId.CopyTo(destination.Slice(48, 16));
        BinaryPrimitives.WriteUInt64BigEndian(destination[64..], counter);
    }

    /// <summary>
    /// Parses a standalone record, validating every cleartext field and
    /// bound before any allocation: the magic (an object without it is not a
    /// standalone record — the caller says so rather than reporting a parse
    /// error), the 04 §2 header rules, ordinal exactly 0, and a total length
    /// that matches the header's stored length to the byte.
    /// </summary>
    /// <exception cref="RecordFormatException">The bytes violate the framing.</exception>
    public static StandaloneRecord Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;

        if (span.Length < PrefixLength + RecordHeader.Length + 16)
        {
            throw new RecordFormatException(
                $"A standalone record is at least {PrefixLength + RecordHeader.Length + 16} bytes; got {span.Length} (ADR-0022).");
        }

        if (!span[..8].SequenceEqual(Magic))
        {
            throw new RecordFormatException("Not a FallbackPlan standalone metadata record — the FBPKSREC magic is absent.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        // Reserved (offset 10) ignored on read (00 §9).
        var generation = new KeyGeneration(BinaryPrimitives.ReadUInt32BigEndian(span[12..]));
        var writerId = WriterId.FromBytes(span.Slice(48, 16));
        var counter = BinaryPrimitives.ReadUInt64BigEndian(span[64..]);

        var header = RecordHeader.Parse(span.Slice(PrefixLength, RecordHeader.Length));

        if (header.Ordinal != 0)
        {
            throw new RecordFormatException(
                $"A standalone record's ordinal is exactly 0; got {header.Ordinal} (ADR-0022 §Decision 1).");
        }

        if (header.StoredLength > (ulong)FormatLimits.MaxMetadataObjectSize)
        {
            throw new RecordFormatException(
                $"stored_length {header.StoredLength} exceeds the 16 MiB metadata bound (specification 00 §8) — refused before allocation.");
        }

        var expectedLength = PrefixLength + RecordHeader.Length + (long)header.StoredLength + 16;
        if (span.Length != expectedLength)
        {
            throw new RecordFormatException(
                $"The object is {span.Length} bytes; the header declares {expectedLength} — data beyond the sealed layout is a damage finding.");
        }

        return new StandaloneRecord(
            formatVersion,
            generation,
            data.Slice(16, BlobSaltLength),
            writerId,
            counter,
            header,
            data.Slice(PrefixLength + RecordHeader.Length, (int)header.StoredLength),
            data.Slice(PrefixLength + RecordHeader.Length + (int)header.StoredLength, 16));
    }
}
