using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// Documents that for shortest-form unsigned-integer map keys — the only keys
/// the format permits (specification 00 §4.2) — CTAP2's length-first ordering
/// and the specification's encoded-byte lexicographic ordering coincide, so a
/// single enforcement layer satisfies rule 3 of 00 §4.1.
/// </summary>
public sealed class MapKeyOrderingTests
{
    // Keys 0, 1, 23, 24, 255, 256, 65536: their canonical encodings straddle
    // every argument-width boundary. Numeric order, length-first order, and
    // encoded-byte lexicographic order are all the same sequence.
    private const string SortedMap = "a700000100170018180018ff00190100001a0001000000";

    [Fact]
    public void Keys_in_encoded_byte_order_are_accepted()
    {
        CanonicalCbor.Validate(Convert.FromHexString(SortedMap));
    }

    [Theory]
    [InlineData("a700000100181800170018ff00190100001a0001000000")] // 24 before 23
    [InlineData("a70000010017001818001901000018ff001a0001000000")] // 256 before 255
    public void Transposed_keys_are_rejected(string hex)
    {
        Assert.Throws<CborFormatException>(() => CanonicalCbor.Validate(Convert.FromHexString(hex)));
    }

    [Fact]
    public void The_writer_produces_exactly_the_documented_order()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(7);
        foreach (var key in new uint[] { 65_536, 256, 255, 24, 23, 1, 0 })
        {
            writer.WriteKey(key);
            writer.WriteUnsignedInteger(0);
        }

        writer.WriteEndMap();

        Assert.Equal(SortedMap, Convert.ToHexStringLower(writer.Encode()));
    }
}
