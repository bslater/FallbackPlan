using System.Buffers.Binary;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Keys;

/// <summary>
/// Serializes and parses the <c>/keys/&lt;key-id&gt;</c> object framing
/// (specification 03 §3):
/// magic <c>FBPKKEYS</c> · <c>format_version</c> u16 · <c>kek_profile</c> u16
/// · 12-byte wrap nonce · 16-byte key id · <c>cbor_length</c> u32 · wrapped
/// bundle · 16-byte tag. All integers big-endian (specification 00 §1).
/// </summary>
public static class KeyObjectFraming
{
    /// <summary>The framing header length: bytes before the wrapped payload.</summary>
    public const int HeaderLength = 44;

    /// <summary>The wrap nonce length.</summary>
    public const int WrapNonceLength = 12;

    /// <summary>The wrap tag length.</summary>
    public const int WrapTagLength = 16;

    /// <summary>
    /// The only wrap profile format v1 permits: <c>aes-256-gcm-v1</c>
    /// (<c>0x0001</c>). The fixed 12-byte nonce field cannot represent any
    /// other approved suite's nonce (specification 03 §3).
    /// </summary>
    public const ushort KekProfileAes256GcmV1 = 0x0001;

    /// <summary>The key-object magic, <c>"FBPKKEYS"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKKEYS"u8;

    /// <summary>
    /// Builds the 28-byte unwrap AAD:
    /// <c>magic ‖ u16(format_version) ‖ u16(kek_profile) ‖ key_id</c>
    /// (specification 03 §3). Binding the identity means a key object renamed
    /// in the store fails authentication.
    /// </summary>
    public static byte[] BuildAad(ushort formatVersion, ushort kekProfile, KeyId keyId)
    {
        var aad = new byte[Magic.Length + sizeof(ushort) + sizeof(ushort) + KeyId.Size];

        Magic.CopyTo(aad);
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(8), formatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(10), kekProfile);
        keyId.CopyTo(aad.AsSpan(12));

        return aad;
    }

    /// <summary>Serializes a key object.</summary>
    /// <exception cref="ArgumentException">A field length is wrong or the bundle exceeds its limit.</exception>
    public static byte[] Serialize(
        ushort formatVersion,
        KeyId keyId,
        ReadOnlySpan<byte> wrapNonce,
        ReadOnlySpan<byte> wrapped,
        ReadOnlySpan<byte> tag)
    {
        if (wrapNonce.Length != WrapNonceLength)
        {
            throw new ArgumentException($"The wrap nonce is exactly {WrapNonceLength} bytes.", nameof(wrapNonce));
        }

        if (tag.Length != WrapTagLength)
        {
            throw new ArgumentException($"The wrap tag is exactly {WrapTagLength} bytes.", nameof(tag));
        }

        if (wrapped.Length > FormatLimits.MaxKeyBundleCborLength)
        {
            throw new ArgumentException(
                $"The wrapped bundle is {wrapped.Length} bytes; the limit is {FormatLimits.MaxKeyBundleCborLength} (specification 00 §8).",
                nameof(wrapped));
        }

        var buffer = new byte[HeaderLength + wrapped.Length + WrapTagLength];
        var span = buffer.AsSpan();

        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], formatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], KekProfileAes256GcmV1);
        wrapNonce.CopyTo(span[12..]);
        keyId.CopyTo(span[24..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[40..], (uint)wrapped.Length);
        wrapped.CopyTo(span[HeaderLength..]);
        tag.CopyTo(span[(HeaderLength + wrapped.Length)..]);

        return buffer;
    }

    /// <summary>
    /// Parses key-object bytes, refusing every framing violation: wrong magic,
    /// an unsupported wrap profile, a bundle length over the limit — checked
    /// before any allocation (specification 00 §8) — or a total length that
    /// does not match the declared bundle length exactly.
    /// </summary>
    /// <exception cref="KeyObjectFormatException">The bytes violate the framing rules.</exception>
    public static KeyObject Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength + WrapTagLength)
        {
            throw new KeyObjectFormatException(
                $"Key object is {data.Length} bytes; the framing alone requires {HeaderLength + WrapTagLength}.");
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new KeyObjectFormatException("Not a key object: the FBPKKEYS magic is absent.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var kekProfile = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);

        if (kekProfile != KekProfileAes256GcmV1)
        {
            throw new KeyObjectFormatException(
                $"Wrap profile 0x{kekProfile:x4} is not permitted: format v1 requires aes-256-gcm-v1 (0x0001) (specification 03 §3).");
        }

        var cborLength = BinaryPrimitives.ReadUInt32BigEndian(data[40..]);

        if (cborLength > FormatLimits.MaxKeyBundleCborLength)
        {
            throw new KeyObjectFormatException(
                $"Wrapped bundle declares {cborLength} bytes; the limit is {FormatLimits.MaxKeyBundleCborLength} (specification 00 §8).");
        }

        var expectedLength = HeaderLength + (int)cborLength + WrapTagLength;

        if (data.Length != expectedLength)
        {
            throw new KeyObjectFormatException(
                $"Key object is {data.Length} bytes; the declared bundle length implies exactly {expectedLength}.");
        }

        return new KeyObject(
            formatVersion,
            kekProfile,
            data.Slice(12, WrapNonceLength).ToArray(),
            KeyId.FromBytes(data.Slice(24, KeyId.Size)),
            data.Slice(HeaderLength, (int)cborLength).ToArray(),
            data.Slice(HeaderLength + (int)cborLength, WrapTagLength).ToArray());
    }
}
