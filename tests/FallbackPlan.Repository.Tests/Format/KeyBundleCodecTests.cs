using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Keys;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the key-bundle CBOR codec (specification 03 §3.1): byte-identical
/// round-trip, unknown-key tolerance (00 §4.3), required-field enforcement,
/// and refusal of non-canonical input via the A2 layer.
/// </summary>
public sealed class KeyBundleCodecTests
{
    private static KeyBundle SampleBundle() =>
        new(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), 3, 1, 1_722_600_000_000);

    [Fact]
    public void Encode_then_decode_round_trips_byte_identically()
    {
        using var bundle = SampleBundle();
        var encoded = KeyBundleCodec.Encode(bundle);

        using var decoded = KeyBundleCodec.Decode(encoded);

        Assert.True(decoded.MasterKey.SequenceEqual(bundle.MasterKey));
        Assert.Equal(bundle.CurrentDataGeneration, decoded.CurrentDataGeneration);
        Assert.Equal(bundle.CurrentMetadataGeneration, decoded.CurrentMetadataGeneration);
        Assert.Equal(bundle.CreatedAt, decoded.CreatedAt);
        Assert.Equal(encoded, KeyBundleCodec.Encode(decoded));
    }

    [Fact]
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

        Assert.Equal(0u, decoded.CurrentDataGeneration);
    }

    [Fact]
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

        Assert.Throws<KeyObjectFormatException>(() => KeyBundleCodec.Decode(writer.Encode()));
    }

    [Fact]
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

        Assert.Throws<CborFormatException>(() => KeyBundleCodec.Decode(writer.Encode()));
    }

    [Fact]
    public void Non_canonical_bundle_bytes_are_refused()
    {
        // Map with duplicate key 1 — violates the deterministic profile.
        Assert.Throws<CborFormatException>(() => KeyBundleCodec.Decode(Convert.FromHexString("a201000100")));
    }

    [Fact]
    public void A_bundle_over_the_size_limit_is_refused_before_parsing()
    {
        Assert.Throws<KeyObjectFormatException>(() => KeyBundleCodec.Decode(new byte[4_097]));
    }
}
