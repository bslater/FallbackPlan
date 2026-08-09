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
    public void Encode_then_decode_round_trips_byte_identically()
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
    public void An_unknown_key_is_tolerated_and_skipped()
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
    public void A_missing_required_field_is_refused()
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
    public void A_wrong_length_master_key_is_refused()
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
    public void Non_canonical_bundle_bytes_are_refused()
    {
        // Map with duplicate key 1 — violates the deterministic profile.
        Assert.ThrowsExactly<CborFormatException>(() => KeyBundleCodec.Decode(Convert.FromHexString("a201000100")));
    }

    [TestMethod]
    public void A_bundle_over_the_size_limit_is_refused_before_parsing()
    {
        Assert.ThrowsExactly<KeyObjectFormatException>(() => KeyBundleCodec.Decode(new byte[4_097]));
    }
}
