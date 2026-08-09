using System.Buffers.Binary;
using FallbackPlan.Domain.Resources;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 32-byte content identifier: the hash of a segment's plaintext under the
/// repository's content-hash profile, computed before compression and before
/// encryption (specification 02 §2; FR-ARCH-003).
/// </summary>
/// <remarks>
/// A content identifier never leaves the trust boundary: it must not be
/// written to any durable object and must not appear in any store key
/// (specification 02 §2; NFR-SEC-004). It may be held in the local catalogue.
/// <see cref="ToString"/> is deliberately redacted so a content identifier
/// cannot leak through logging or diagnostics by accident.
/// </remarks>
public readonly struct ContentId : IEquatable<ContentId>
{
    /// <summary>The exact size of a content identifier in bytes.</summary>
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private ContentId(ulong a, ulong b, ulong c, ulong d)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    /// <summary>
    /// Creates a content identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 32 bytes.</exception>
    public static ContentId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Strings.FormatContentId_ContentIdentifierExactlyBytesGot(Size, bytes.Length), nameof(bytes));
        }

        return new ContentId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
    }

    /// <summary>
    /// Copies the identifier's bytes into <paramref name="destination"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than 32 bytes.</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(Strings.FormatBlobId_DestinationMustHoldLeastBytes(Size), nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _d);
    }

    /// <summary>Returns the identifier as a new 32-byte array.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <inheritdoc />
    public bool Equals(ContentId other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    /// <summary>
    /// Deliberately redacted: a content identifier must never reach a log or a
    /// durable object (specification 02 §2; NFR-SEC-004).
    /// </summary>
    public override string ToString() => "content-id(redacted)";

    public static bool operator ==(ContentId left, ContentId right) => left.Equals(right);

    public static bool operator !=(ContentId left, ContentId right) => !left.Equals(right);
}
