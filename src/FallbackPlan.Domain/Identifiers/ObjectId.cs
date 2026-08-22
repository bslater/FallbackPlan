using System.Buffers.Binary;
using FallbackPlan.Domain.Resources;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 32-byte object identifier: the keyed, type-separated form of a content
/// identifier that names records, manifests, and index entries outside the
/// trust boundary (specification 02 §3; NFR-SEC-004).
/// </summary>
/// <remarks>
/// This type only carries the derived bytes. The derivation itself — an HMAC
/// under the content-ID key with an object-type domain separator — lives in
/// the crypto layer, which is the only place key material is handled.
/// </remarks>
public readonly struct ObjectId : IEquatable<ObjectId>, Diagnostics.IRedactedValue
{
    /// <summary>The exact size of an object identifier in bytes.</summary>
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private ObjectId(ulong a, ulong b, ulong c, ulong d)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    /// <summary>
    /// Creates an object identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 32 bytes.</exception>
    public static ObjectId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Strings.FormatObjectId_ObjectIdentifierExactlyBytesGot(Size, bytes.Length), nameof(bytes));
        }

        return new ObjectId(
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

    /// <summary>
    /// Renders the identifier as lowercase unpadded base32 — 52 characters —
    /// for use in store keys (specification 02 §5).
    /// </summary>
    public string ToBase32() => Base32.Encode(ToArray());

    /// <inheritdoc />
    public bool Equals(ObjectId other) =>
        _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);

    /// <summary>Renders the identifier as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    /// <summary>
    /// The rendering that may leave the machine (ADR-0043 §4): a stable short
    /// prefix, correlatable across records without being the correlatable
    /// identifier NFR-PRIV-002 keeps off the wire.
    /// </summary>
    public string ToRedactedString() =>
        Diagnostics.Redaction.Shorten("obj", ToString(), 8);

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);

    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
}
