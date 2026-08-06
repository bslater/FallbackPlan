using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Keys;

/// <summary>
/// A parsed <c>/keys/&lt;key-id&gt;</c> object (specification 03 §3): the
/// framing fields plus the still-wrapped bundle. Unwrapping is the crypto
/// layer's job; this type never sees key material.
/// </summary>
public sealed class KeyObject
{
    internal KeyObject(ushort formatVersion, ushort kekProfile, byte[] wrapNonce, KeyId keyId, byte[] wrapped, byte[] tag)
    {
        FormatVersion = formatVersion;
        KekProfile = kekProfile;
        WrapNonceBytes = wrapNonce;
        KeyId = keyId;
        WrappedBytes = wrapped;
        TagBytes = tag;
    }

    /// <summary>The object's format version.</summary>
    public ushort FormatVersion { get; }

    /// <summary>The wrap profile — <c>0x0001</c> (AES-256-GCM) in format v1.</summary>
    public ushort KekProfile { get; }

    /// <summary>The key-object identity, bound into the unwrap AAD.</summary>
    public KeyId KeyId { get; }

    private byte[] WrapNonceBytes { get; }

    private byte[] WrappedBytes { get; }

    private byte[] TagBytes { get; }

    /// <summary>The 12-byte wrap nonce.</summary>
    public ReadOnlySpan<byte> WrapNonce => WrapNonceBytes;

    /// <summary>The AEAD ciphertext of the CBOR key bundle.</summary>
    public ReadOnlySpan<byte> Wrapped => WrappedBytes;

    /// <summary>The 16-byte wrap tag.</summary>
    public ReadOnlySpan<byte> Tag => TagBytes;
}
