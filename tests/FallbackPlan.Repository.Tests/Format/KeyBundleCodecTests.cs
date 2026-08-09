using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Keys;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the key-bundle CBOR codec (specification 03 §3.1): byte-identical
/// round-trip, unknown-key tolerance (00 §4.3), required-field enforcement,
/// and refusal of non-canonical input via the A2 layer.
/// </summary>
[TestClass]
public sealed class KeyBundleCodecTests
{
    private static KeyBundle SampleBundle() =>
        new(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), 3, 1, 1_722_600_000_000);

    [TestMethod]
    public void KeyBundle_EncodedAndDecoded_RoundTripsByteIdentically()
    {
        using var bundle = SampleBundle();
        var encoded = KeyBundleCodec.Encode(bundle);

        using var decoded = KeyBundleCodec.Decode(encoded);

        Assert.IsTrue(decoded.MasterKey.SequenceEqual(bundle.MasterKey));
        Assert.AreEqual(bundle.CurrentDataGeneration, decoded.CurrentDataGeneration);
        Assert.AreEqual(bundle.CurrentMetadataGeneration, decoded.CurrentMetadataGeneration);
        Assert.AreEqual(bundle.CreatedAt, decoded.CreatedAt);
        SequenceAssert.AreEqual(encoded, KeyBundleCodec.Encode(decoded));
    }

    [TestMethod]
    public void KeyBundle_CarriesAnUnknownKey_SkipsItAndDecodesTheRest()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(5);
        writer.WriteKey(1);
        writer.WriteByteString(new byte[32]);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(5); // a future field this reader does not know
        writer.WriteByteString([0xEE]);
        writer.WriteEndMap();

        using var decoded = KeyBundleCodec.Decode(writer.Encode());

        Assert.AreEqual(0u, decoded.CurrentDataGeneration);
    }

    [TestMethod]
    public void KeyBundle_ARequiredFieldIsMissing_IsRefused()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(3);
        writer.WriteKey(1);
        writer.WriteByteString(new byte[32]);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(0);
        writer.WriteEndMap(); // created_at absent

        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyBundleCodec.Decode(writer.Encode()));
    }

    [TestMethod]
    public void KeyBundle_MasterKeyIsTheWrongLength_IsRefused()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(4);
        writer.WriteKey(1);
        writer.WriteByteString(new byte[31]);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(0);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(0);
        writer.WriteEndMap();

        Assert.ThrowsExactly<CborFormatException>(() => KeyBundleCodec.Decode(writer.Encode()));
    }

    [TestMethod]
    public void KeyBundle_BytesAreNonCanonical_AreRefused()
    {
        // Map with duplicate key 1 — violates the deterministic profile.
        Assert.ThrowsExactly<CborFormatException>(() => KeyBundleCodec.Decode(Convert.FromHexString("a201000100")));
    }

    [TestMethod]
    public void KeyBundle_ExceedsTheSizeLimit_IsRefusedBeforeParsing()
    {
        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyBundleCodec.Decode(new byte[4_097]));
    }
}
