using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the key-object framing byte-exactly against specification 03 §3:
/// field offsets, the v1 wrap-profile restriction, the pre-allocation length
/// limit, and the AAD bytes.
/// </summary>
[TestClass]
public sealed class KeyObjectFramingTests
{
    private static readonly KeyId SomeKeyId = KeyId.FromBytes(Convert.FromHexString("101112131415161718191a1b1c1d1e1f"));

    private static byte[] SerializeSample(byte[] wrapped)
    {
        var nonce = Enumerable.Range(0x20, 12).Select(value => (byte)value).ToArray();
        var tag = Enumerable.Range(0x40, 16).Select(value => (byte)value).ToArray();

        return KeyObjectFraming.Serialize(1, SomeKeyId, nonce, wrapped, tag);
    }

    [TestMethod]
    public void KeyObjectFraming_ASerialisedObject_PlacesEveryFieldAtItsSpecifiedOffset()
    {
        var wrapped = new byte[] { 0xAA, 0xBB, 0xCC };
        var bytes = SerializeSample(wrapped);

        SequenceAssert.AreEqual("FBPKKEYS"u8.ToArray(), bytes[..8]);                       // magic
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[8..10]);                  // format_version u16
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[10..12]);                 // kek_profile u16
        Assert.AreEqual(0x20, bytes[12]);                                          // wrap_nonce @12
        Assert.AreEqual(0x10, bytes[24]);                                          // key_id @24
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x03 }, bytes[40..44]);     // cbor_length u32
        SequenceAssert.AreEqual(wrapped, bytes[44..47]);                                   // wrapped
        Assert.AreEqual(0x40, bytes[47]);                                          // tag
        Assert.AreEqual(44 + 3 + 16, bytes.Length);
    }

    [TestMethod]
    public void KeyObjectFraming_SerialisedThenParsed_RoundTrips()
    {
        var wrapped = "wrapped-bundle"u8.ToArray();
        var parsed = KeyObjectFraming.Parse(SerializeSample(wrapped));

        Assert.AreEqual(1, parsed.FormatVersion);
        Assert.AreEqual(KeyObjectFraming.KekProfileAes256GcmV1, parsed.KekProfile);
        Assert.AreEqual(SomeKeyId, parsed.KeyId);
        SequenceAssert.AreEqual(wrapped, parsed.Wrapped.ToArray());
    }

    [TestMethod]
    public void KeyObjectFraming_MagicIsWrong_SaysItIsNotAKeyObject()
    {
        var bytes = SerializeSample([0x01]);
        bytes[0] = (byte)'X';

        var exception = Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));

        Assert.Contains("Not a key object", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void KeyObjectFraming_WrapProfileIsNotAesGcm_IsRefusedInV1()
    {
        var bytes = SerializeSample([0x01]);
        bytes[11] = 0x02; // xchacha20-poly1305-v1 — its 24-byte nonce cannot fit the field

        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));
    }

    [TestMethod]
    public void KeyObjectFraming_BundleLengthExceedsTheLimit_IsRefusedBeforeAllocation()
    {
        var bytes = SerializeSample([0x01]);
        bytes[40] = 0xFF;
        bytes[41] = 0xFF;
        bytes[42] = 0xFF;
        bytes[43] = 0xFF; // declares ~4 GiB — must be refused from the prefix alone

        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes));
    }

    [TestMethod]
    public void KeyObjectFraming_TotalLengthDisagreesWithTheDeclaredBundle_IsRefused()
    {
        var bytes = SerializeSample([0x01, 0x02]);

        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyObjectFraming.Parse(bytes.AsSpan()[..^1]));
        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyObjectFraming.Parse([.. bytes, 0x00]));
    }

    [TestMethod]
    public void KeyObjectFraming_TheAssociatedData_IsMagicVersionProfileAndKeyId()
    {
        var aad = KeyObjectFraming.BuildAad(1, KeyObjectFraming.KekProfileAes256GcmV1, SomeKeyId);

        Assert.AreEqual(28, aad.Length);
        SequenceAssert.AreEqual("FBPKKEYS"u8.ToArray(), aad[..8]);
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01, 0x00, 0x01 }, aad[8..12]);
        SequenceAssert.AreEqual(SomeKeyId.ToArray(), aad[12..28]);
    }
}
