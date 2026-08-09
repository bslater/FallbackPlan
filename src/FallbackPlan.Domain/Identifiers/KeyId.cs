using System.Buffers.Binary;
using FallbackPlan.Domain.Resources;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte identity of a key object under <c>/keys/</c> (specification
/// 03 §3). It is bound into the key object's unwrap AAD, so a key object
/// renamed in the store fails authentication.
/// </summary>
public readonly struct KeyId : IEquatable<KeyId>
{
    /// <summary>The exact size of a key identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private KeyId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// Creates a key identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static KeyId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Strings.FormatKeyId_KeyIdentifierExactlyBytesGot(Size, bytes.Length), nameof(bytes));
        }

        return new KeyId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

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
    public bool Equals(KeyId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Renders the identifier as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    public static bool operator ==(KeyId left, KeyId right) => left.Equals(right);

    public static bool operator !=(KeyId left, KeyId right) => !left.Equals(right);
}
