using System.Buffers.Binary;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte repository identity, drawn randomly at creation and never
/// reused (specification 01 §3.2). It binds every record's AAD to the
/// repository (specification 04 §4), so a record copied between repositories
/// fails authentication.
/// </summary>
public readonly struct RepositoryId : IEquatable<RepositoryId>
{
    /// <summary>The exact size of a repository identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private RepositoryId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// Creates a repository identifier from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static RepositoryId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A repository identifier is exactly {Size} bytes; got {bytes.Length}.", nameof(bytes));
        }

        return new RepositoryId(
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

    /// <summary>
    /// Renders the identifier as lowercase unpadded base32 — 26 characters —
    /// for use in store keys (specification 02 §5).
    /// </summary>
    public string ToBase32() => Base32.Encode(ToArray());

    /// <inheritdoc />
    public bool Equals(RepositoryId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RepositoryId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Renders the identifier as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    public static bool operator ==(RepositoryId left, RepositoryId right) => left.Equals(right);

    public static bool operator !=(RepositoryId left, RepositoryId right) => !left.Equals(right);
}
