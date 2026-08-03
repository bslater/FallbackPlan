using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// The rejection suite for the deterministic CBOR profile (specification 00
/// §4.1–§4.2; NFR-PORT-003, NFR-COMP-004). Object identifiers derive from
/// encoded bytes, so a lenient decoder silently permits two encodings of the
/// same object — these tests were written before the encoder, per the phase-0
/// plan, and every one must refuse its input rather than accept it leniently.
/// </summary>
public sealed class CanonicalCborRejectionTests
{
    private static void AssertRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex);

        Assert.Throws<CborFormatException>(() => CanonicalCbor.Validate(bytes));
    }

    [Fact]
    public void Rejects_indefinite_length_byte_string() => AssertRejected("5f4100ff");

    [Fact]
    public void Rejects_indefinite_length_text_string() => AssertRejected("7f6161ff");

    [Fact]
    public void Rejects_indefinite_length_array() => AssertRejected("9f00ff");

    [Fact]
    public void Rejects_indefinite_length_map() => AssertRejected("bf0100ff");

    [Fact]
    public void Rejects_non_shortest_unsigned_integer_zero_in_two_bytes() => AssertRejected("1800");

    [Fact]
    public void Rejects_non_shortest_unsigned_integer_twenty_four_in_three_bytes() => AssertRejected("190018");

    [Fact]
    public void Rejects_non_shortest_byte_string_length_prefix() => AssertRejected("5803010203");

    [Fact]
    public void Rejects_unsorted_map_keys() => AssertRejected("a202000100");

    [Fact]
    public void Rejects_duplicate_map_keys() => AssertRejected("a201000100");

    [Fact]
    public void Rejects_half_precision_float() => AssertRejected("f93c00");

    [Fact]
    public void Rejects_single_precision_float() => AssertRejected("fa47c35000");

    [Fact]
    public void Rejects_double_precision_float() => AssertRejected("fb3ff199999999999a");

    [Fact]
    public void Rejects_tagged_value() => AssertRejected("c060");

    [Fact]
    public void Rejects_text_string_map_key() => AssertRejected("a1616100");

    [Fact]
    public void Rejects_negative_integer_map_key() => AssertRejected("a12000");

    [Fact]
    public void Rejects_null_value() => AssertRejected("f6");

    [Fact]
    public void Rejects_undefined_value() => AssertRejected("f7");

    [Fact]
    public void Rejects_trailing_bytes_after_the_root_value() => AssertRejected("0000");

    [Fact]
    public void Rejects_truncated_input() => AssertRejected("a1");

    [Fact]
    public void Rejects_float_nested_inside_a_skipped_unknown_value()
    {
        // Map {1: [1.0h]} — the float hides inside a value a reader with an
        // unknown-key schema would skip; the skip walk must still refuse it.
        var bytes = Convert.FromHexString("a10181f93c00");

        var reader = new CanonicalCborReader(bytes);
        Assert.Equal(1, reader.ReadStartMap());
        Assert.Equal(1u, reader.ReadKey());

        Assert.Throws<CborFormatException>(reader.SkipValue);
    }

    [Fact]
    public void Rejects_non_shortest_integer_nested_inside_a_skipped_value()
    {
        // Map {1: 0x1800} — non-shortest zero inside the skipped value.
        var bytes = Convert.FromHexString("a1011800");

        var reader = new CanonicalCborReader(bytes);
        reader.ReadStartMap();
        reader.ReadKey();

        Assert.Throws<CborFormatException>(reader.SkipValue);
    }
}
