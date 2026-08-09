using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// Record nonce construction (specification 04 §3): the big-endian ordinal in
/// the trailing bytes, zeros elsewhere, no randomness. Nonce uniqueness under
/// one blob key is structural — ordinals are unique within a blob and each
/// blob has its own key — which is exactly why the spool rules exist to
/// prevent the same <c>(key, ordinal)</c> from ever covering two byte
/// strings (05 §6.1).
/// </summary>
public static class RecordNonce
{
    /// <summary>The AES-256-GCM nonce length.</summary>
    public const int AesGcmLength = 12;

    /// <summary>The XChaCha20-Poly1305 nonce length.</summary>
    public const int XChaChaLength = 24;

    /// <summary>
    /// Writes the nonce for <paramref name="ordinal"/> into a 12-byte
    /// (AES-256-GCM) or 24-byte (XChaCha20-Poly1305) destination — for the
    /// longer form the ordinal occupies the last 12 bytes and the first 12
    /// are zero (specification 04 §3).
    /// </summary>
    /// <exception cref="ArgumentException">The destination is neither 12 nor 24 bytes.</exception>
    public static void Write(uint ordinal, Span<byte> destination)
    {
        if (destination.Length is not (AesGcmLength or XChaChaLength))
        {
            throw new ArgumentException(Strings.FormatRecordNonce_RecordNonceBytesGot(AesGcmLength, XChaChaLength, destination.Length),
                nameof(destination));
        }

        destination.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(destination[^4..], ordinal);
    }

    /// <summary>
    /// Writes the reserved footer nonce — twelve <c>0xFF</c> bytes,
    /// unreachable by any record because ordinals never exceed 65 535
    /// (specification 05 §3).
    /// </summary>
    /// <exception cref="ArgumentException">The destination is not 12 bytes.</exception>
    public static void WriteFooterNonce(Span<byte> destination)
    {
        if (destination.Length != AesGcmLength)
        {
            throw new ArgumentException(Strings.FormatRecordNonce_FooterNonceBytesGot(AesGcmLength, destination.Length),
                nameof(destination));
        }

        destination.Fill(0xFF);
    }
}
