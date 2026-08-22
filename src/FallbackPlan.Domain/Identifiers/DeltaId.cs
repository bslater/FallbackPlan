using System.Buffers.Binary;
using FallbackPlan.Domain.Resources;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte identity of an index delta (specification 07 §2; ADR-0022
/// §Decision 2): drawn from a CSPRNG at publication — deliberately not
/// content-derived, because a retried publication re-seals under a fresh
/// salt — and rendered in the store path as 26 lowercase base32 characters.
/// </summary>
public readonly struct DeltaId : IEquatable<DeltaId>, Diagnostics.IRedactedValue
{
    /// <summary>The exact size of a delta identifier in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private DeltaId(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>Creates a delta identifier from exactly <see cref="Size"/> bytes.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static DeltaId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException(Strings.FormatDeltaId_DeltaIdentifierExactlyBytesGot(Size, bytes.Length), nameof(bytes));
        }

        return new DeltaId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>Allocates a fresh random identifier — one per logical publication, not per attempt.</summary>
    public static DeltaId NewRandom()
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

    /// <summary>The 26-character lowercase base32 path rendering (specification 00 §6).</summary>
    public string ToBase32() => Base32.Encode(ToArray());

    /// <inheritdoc />
    public bool Equals(DeltaId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeltaId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <inheritdoc />
    public override string ToString() => ToBase32();

    /// <summary>
    /// The rendering that may leave the machine (ADR-0043 §4): a stable short
    /// prefix, correlatable across records without being the correlatable
    /// identifier NFR-PRIV-002 keeps off the wire.
    /// </summary>
    public string ToRedactedString() =>
        Diagnostics.Redaction.Shorten("delta", ToString(), 6);

    public static bool operator ==(DeltaId left, DeltaId right) => left.Equals(right);

    public static bool operator !=(DeltaId left, DeltaId right) => !left.Equals(right);
}
