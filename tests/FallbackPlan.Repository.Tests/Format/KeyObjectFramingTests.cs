using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Keys;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the key-object framing byte-exactly against specification 03 §3:
/// field offsets, the v1 wrap-profile restriction, the pre-allocation length
/// limit, and the AAD bytes.
/// </summary>
public sealed class KeyObjectFramingTests
{
    private static readonly KeyId SomeKeyId = KeyId.FromBytes(Convert.FromHexString("101112131415161718191a1b1c1d1e1f"));

    private static byte[] SerializeSample(byte[] wrapped)
    {
        var nonce = Enumerable.Range(0x20, 12).Select(value => (byte)value).ToArray();
        var tag = Enumerable.Range(0x40, 16).Select(value => (byte)value).ToArray();

        return KeyObjectFraming.Serialize(1, SomeKeyId, nonce, wrapped, tag);
    }

    [Fact]
    public void Field_offsets_are_byte_exact()
    {
        var wrapped = new byte[] { 0xAA, 0xBB, 0xCC };
        var bytes = SerializeSample(wrapped);

        Assert.Equal("FBPKKEYS"u8.ToArray(), bytes[..8]);                       // magic
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[8..10]);                  // format_version u16
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[10..12]);                 // kek_profile u16
        Assert.Equal(0x20, bytes[12]);                                          // wrap_nonce @12
        Assert.Equal(0x10, bytes[24]);                                          // key_id @24
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03 }, bytes[40..44]);     // cbor_length u32
        Assert.Equal(wrapped, bytes[44..47]);                                   // wrapped
        Assert.Equal(0x40, bytes[47]);                                          // tag
        Assert.Equal(44 + 3 + 16, bytes.Length);
    }

    [Fact]
    public void Parse_round_trips_serialize()
    {
        var wrapped = "wrapped-bundle"u8.ToArray();
        var parsed = KeyObjectFraming.Parse(SerializeSample(wrapped));

        Assert.Equal(1, parsed.FormatVersion);
        Assert.Equal(KeyObjectFraming.KekProfileAes256GcmV1, parsed.KekProfile);
        Assert.Equal(SomeKeyId, parsed.KeyId);
        Assert.Equal(wrapped, parsed.Wrapped.ToArray());
    }

    [Fact]
    public void Wrong_magic_is_named_as_not_a_key_object()
    {
        var bytes = SerializeSample([0x01]);
        bytes[0] = (byte)'X';

        var exception = Assert.Throws<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));

        Assert.Contains("Not a key object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrap_profile_other_than_aes_gcm_is_refused_in_v1()
    {
        var bytes = SerializeSample([0x01]);
        bytes[11] = 0x02; // xchacha20-poly1305-v1 — its 24-byte nonce cannot fit the field

        Assert.Throws<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));
    }

    [Fact]
    public void A_bundle_length_over_the_limit_is_refused_before_allocation()
    {
        var bytes = SerializeSample([0x01]);
        bytes[40] = 0xFF;
        bytes[41] = 0xFF;
        bytes[42] = 0xFF;
        bytes[43] = 0xFF; // declares ~4 GiB — must be refused from the prefix alone

        Assert.Throws<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));
    }

    [Fact]
    public void A_total_length_mismatching_the_declared_bundle_is_refused()
    {
        var bytes = SerializeSample([0x01, 0x02]);

        Assert.Throws<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes.AsSpan()[..^1]));
        Assert.Throws<KeyObjectFormatException>(() => KeyObjectFraming.Parse([.. bytes, 0x00]));
    }

    [Fact]
    public void The_aad_is_magic_version_profile_and_key_id()
    {
        var aad = KeyObjectFraming.BuildAad(1, KeyObjectFraming.KekProfileAes256GcmV1, SomeKeyId);

        Assert.Equal(28, aad.Length);
        Assert.Equal("FBPKKEYS"u8.ToArray(), aad[..8]);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x01 }, aad[8..12]);
        Assert.Equal(SomeKeyId.ToArray(), aad[12..28]);
    }
}
