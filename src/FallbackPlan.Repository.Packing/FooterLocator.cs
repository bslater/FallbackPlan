using System.Buffers.Binary;
using FallbackPlan.Repository.Packing.Resources;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The 16-byte footer locator at the very end of a blob (specification
/// 05 §4): <c>footer_offset u64 ‖ digest_prefix bytes[8]</c>. It is what
/// makes the footer reachable in two range reads. The digest prefix is
/// unkeyed and truncated — a cheap corruption check, never an integrity
/// guarantee.
/// </summary>
/// <remarks>
/// The digest preimage is every sealed byte <em>before</em> the locator —
/// <c>[0, blobLength − 16)</c>. The specification's "complete sealed
/// representation" (05 §5) cannot include the locator itself without
/// circularity; this reading is recorded here pending a specification
/// erratum and vector.
/// </remarks>
public readonly record struct FooterLocator(ulong FooterOffset, ulong DigestPrefix)
{
    /// <summary>The locator length: 16 bytes.</summary>
    public const int Length = 16;

    /// <summary>Writes the 16 locator bytes.</summary>
    /// <exception cref="ArgumentException">The destination is not exactly 16 bytes.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(Strings.Formatstruct_FooterLocatorExactlyBytesGot(Length, destination.Length), nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, FooterOffset);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], DigestPrefix);
    }

    /// <summary>
    /// Parses a locator, refusing a footer offset that cannot fall inside a
    /// blob of <paramref name="blobLength"/> bytes — before the envelope's
    /// end or at or past the locator itself.
    /// </summary>
    /// <exception cref="BlobFormatException">The locator is damaged.</exception>
    public static FooterLocator Parse(ReadOnlySpan<byte> data, long blobLength)
    {
        if (data.Length != Length)
        {
            throw new BlobFormatException(Strings.Formatstruct_FooterLocatorBytesGot(Length, data.Length));
        }

        var footerOffset = BinaryPrimitives.ReadUInt64BigEndian(data);

        if (footerOffset < BlobEnvelope.Length || footerOffset >= (ulong)(blobLength - Length))
        {
            throw new BlobFormatException(Strings.Formatstruct_FooterOffsetDoesNotFall(footerOffset, blobLength));
        }

        return new FooterLocator(footerOffset, BinaryPrimitives.ReadUInt64BigEndian(data[8..]));
    }
}
