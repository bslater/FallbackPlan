using System.Buffers.Binary;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte identity of a writer — a device or process that appends to the
/// repository (specification 02). A writer identifier is a blob-key derivation
/// input and a journal path component; it is carried in every blob's cleartext
/// envelope (specification 05 §2).
/// </summary>
public readonly struct WriterId : IEquatable<WriterId>
{
    /// <summary>The exact size of a writer identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private WriterId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// Creates a writer identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static WriterId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A writer identifier is exactly {Size} bytes; got {bytes.Length}.", nameof(bytes));
        }

        return new WriterId(
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
            throw new ArgumentException($"Destination must hold at least {Size} bytes.", nameof(destination));
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
    public bool Equals(WriterId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WriterId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Renders the identifier as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    public static bool operator ==(WriterId left, WriterId right) => left.Equals(right);

    public static bool operator !=(WriterId left, WriterId right) => !left.Equals(right);
}
