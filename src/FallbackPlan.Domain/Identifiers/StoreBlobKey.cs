using System.Buffers.Binary;

namespace FallbackPlan.Domain.Identifiers;

/// <summary>
/// The 16-byte keyed rendering of a blob identifier for use in store keys
/// (specification 02 §4.3). A blob identifier's leading bytes may be writer
/// identity, so the raw identifier must never appear in a store key; this
/// HMAC-derived form hides writer identity from the store namespace.
/// </summary>
/// <remarks>
/// This type only carries the derived bytes. The derivation itself — an HMAC
/// under the key-ID key — lives in the crypto layer, which is the only place
/// key material is handled.
/// </remarks>
public readonly struct StoreBlobKey : IEquatable<StoreBlobKey>
{
    /// <summary>The exact size of a store blob key in bytes.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    private StoreBlobKey(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <summary>
    /// Creates a store blob key from exactly <see cref="Size"/> bytes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static StoreBlobKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A store blob key is exactly {Size} bytes; got {bytes.Length}.", nameof(bytes));
        }

        return new StoreBlobKey(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>
    /// Copies the key's bytes into <paramref name="destination"/>.
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

    /// <summary>Returns the key as a new 16-byte array.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Renders the key as lowercase unpadded base32 — 26 characters — the form
    /// used in <c>/blobs/…</c> store paths (specification 02 §5).
    /// </summary>
    public string ToBase32() => Base32.Encode(ToArray());

    /// <inheritdoc />
    public bool Equals(StoreBlobKey other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StoreBlobKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Renders the key as lowercase hex for diagnostics.</summary>
    public override string ToString() => Convert.ToHexStringLower(ToArray());

    public static bool operator ==(StoreBlobKey left, StoreBlobKey right) => left.Equals(right);

    public static bool operator !=(StoreBlobKey left, StoreBlobKey right) => !left.Equals(right);
}
