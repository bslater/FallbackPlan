using System.Buffers.Binary;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte identity of an index checkpoint (specification 07 §5;
/// ADR-0022 §Decision 2): CSPRNG-allocated at publication and rendered in
/// the store path as 26 lowercase base32 characters, exactly as
/// <see cref="DeltaId"/> is.
/// </summary>
public readonly struct CheckpointId : IEquatable<CheckpointId>
{
    /// <summary>The exact size of a checkpoint identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private CheckpointId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>Creates a checkpoint identifier from exactly <see cref="Size"/> bytes.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static CheckpointId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A checkpoint identifier is exactly {Size} bytes; got {bytes.Length}.", nameof(bytes));
        }

        return new CheckpointId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>Allocates a fresh random identifier — one per logical publication, not per attempt.</summary>
    public static CheckpointId NewRandom()
    {
        Span<byte> bytes = stackalloc byte[Size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return FromBytes(bytes);
    }

    /// <summary>Copies the identifier's bytes into <paramref name="destination"/>.</summary>
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

    /// <summary>The 26-character lowercase base32 path rendering (specification 00 §6).</summary>
    public string ToBase32() => Base32.Encode(ToArray());

    /// <inheritdoc />
    public bool Equals(CheckpointId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CheckpointId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <inheritdoc />
    public override string ToString() => ToBase32();

    public static bool operator ==(CheckpointId left, CheckpointId right) => left.Equals(right);

    public static bool operator !=(CheckpointId left, CheckpointId right) => !left.Equals(right);
}
