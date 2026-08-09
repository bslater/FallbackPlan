using System.Buffers.Binary;
using FallbackPlan.Domain.Resources;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte identity of a blob, allocated by the writer rather than derived
/// from content (specification 02 §4) so it exists before the blob does — the
/// property the write-intent mechanism depends on (specification 08 §3.1;
/// FR-SNP-006).
/// </summary>
/// <remarks>
/// <para>
/// Two allocation strategies satisfy the specification: the structured form
/// <c>writer_id[0..8] ‖ u64(blob_counter)</c> (<see cref="FromWriterCounter"/>)
/// and 16 bytes from a CSPRNG (<see cref="FromRandom"/>). Nothing downstream
/// depends on the choice — every blob-key derivation input is carried in the
/// blob's cleartext envelope.
/// </para>
/// <para>
/// A blob identifier carries no integrity property (specification 02 §4.2),
/// and it must never be used directly as a store key: the store rendering is
/// the keyed <c>store_blob_key</c> (specification 02 §4.3).
/// </para>
/// </remarks>
public readonly struct BlobId : IEquatable<BlobId>
{
    /// <summary>The exact size of a blob identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private BlobId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// Creates a blob identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static BlobId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Strings.FormatBlobId_BlobIdentifierExactlyBytesGot(Size, bytes.Length), nameof(bytes));
        }

        return new BlobId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>
    /// Allocates the structured form: the first eight bytes of
    /// <paramref name="writer"/> followed by the big-endian
    /// <paramref name="blobCounter"/> (specification 02 §4).
    /// </summary>
    public static BlobId FromWriterCounter(WriterId writer, ulong blobCounter)
    {
        Span<byte> writerBytes = stackalloc byte[WriterId.Size];
        writer.CopyTo(writerBytes);

        return new BlobId(BinaryPrimitives.ReadUInt64BigEndian(writerBytes), blobCounter);
    }

    /// <summary>
    /// Allocates the random form from exactly 16 caller-supplied CSPRNG bytes.
    /// The caller owns the randomness source; this type never draws its own.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="randomBytes"/> is not exactly 16 bytes.</exception>
    public static BlobId FromRandom(ReadOnlySpan<byte> randomBytes) => FromBytes(randomBytes);

    /// <summary>
    /// Copies the identifier's bytes into <paramref name="destination"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 16 bytes.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(Strings.FormatBlobId_DestinationMustHoldLeastBytes(Size), nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _low);
    }

    /// <summary>Returns the identifier as a new 16-byte array.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <inheritdoc />
    public bool Equals(BlobId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BlobId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Renders the identifier as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    public static bool operator ==(BlobId left, BlobId right) => left.Equals(right);

    public static bool operator !=(BlobId left, BlobId right) => !left.Equals(right);
}
