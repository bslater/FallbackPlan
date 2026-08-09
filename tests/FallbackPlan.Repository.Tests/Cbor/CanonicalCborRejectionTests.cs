using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// The rejection suite for the deterministic CBOR profile (specification 00
/// §4.1–§4.2; NFR-PORT-003, NFR-COMP-004). Object identifiers derive from
/// encoded bytes, so a lenient decoder silently permits two encodings of the
/// same object — these tests were written before the encoder, per the phase-0
/// plan, and every one must refuse its input rather than accept it leniently.
/// </summary>
[TestClass]
public sealed class CanonicalCborRejectionTests
{
    private static void AssertRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex);

        Assert.ThrowsExactly<CborFormatException>(() => CanonicalCbor.Validate(bytes));
    }

    [TestMethod]
    public void CanonicalCbor_IndefiniteLengthByteString_IsRejected() => AssertRejected("5f4100ff");

    [TestMethod]
    public void CanonicalCbor_IndefiniteLengthTextString_IsRejected() => AssertRejected("7f6161ff");

    [TestMethod]
    public void CanonicalCbor_IndefiniteLengthArray_IsRejected() => AssertRejected("9f00ff");

    [TestMethod]
    public void CanonicalCbor_IndefiniteLengthMap_IsRejected() => AssertRejected("bf0100ff");

    [TestMethod]
    public void CanonicalCbor_ZeroEncodedInTwoBytes_IsRejected() => AssertRejected("1800");

    [TestMethod]
    public void CanonicalCbor_TwentyFourEncodedInThreeBytes_IsRejected() => AssertRejected("190018");

    [TestMethod]
    public void CanonicalCbor_NonShortestByteStringLengthPrefix_IsRejected() => AssertRejected("5803010203");

    [TestMethod]
    public void CanonicalCbor_UnsortedMapKeys_AreRejected() => AssertRejected("a202000100");

    [TestMethod]
    public void CanonicalCbor_DuplicateMapKeys_AreRejected() => AssertRejected("a201000100");

    [TestMethod]
    public void CanonicalCbor_HalfPrecisionFloat_IsRejected() => AssertRejected("f93c00");

    [TestMethod]
    public void CanonicalCbor_SinglePrecisionFloat_IsRejected() => AssertRejected("fa47c35000");

    [TestMethod]
    public void CanonicalCbor_DoublePrecisionFloat_IsRejected() => AssertRejected("fb3ff199999999999a");

    [TestMethod]
    public void CanonicalCbor_TaggedValue_IsRejected() => AssertRejected("c060");

    [TestMethod]
    public void CanonicalCbor_TextStringMapKey_IsRejected() => AssertRejected("a1616100");

    [TestMethod]
    public void CanonicalCbor_NegativeIntegerMapKey_IsRejected() => AssertRejected("a12000");

    [TestMethod]
    public void CanonicalCbor_NullValue_IsRejected() => AssertRejected("f6");

    [TestMethod]
    public void CanonicalCbor_UndefinedValue_IsRejected() => AssertRejected("f7");

    [TestMethod]
    public void CanonicalCbor_TrailingBytesAfterTheRootValue_AreRejected() => AssertRejected("0000");

    [TestMethod]
    public void CanonicalCbor_TruncatedInput_IsRejected() => AssertRejected("a1");

    [TestMethod]
    public void CanonicalCbor_FloatNestedInsideASkippedValue_IsRejected()
    {
        // Map {1: [1.0h]} — the float hides inside a value a reader with an
        // unknown-key schema would skip; the skip walk must still refuse it.
        var bytes = Convert.FromHexString("a10181f93c00");

        var reader = new CanonicalCborReader(bytes);
        Assert.AreEqual(1, reader.ReadStartMap());
        Assert.AreEqual(1u, reader.ReadKey());

        Assert.ThrowsExactly<CborFormatException>(reader.SkipValue);
    }

    [TestMethod]
    public void CanonicalCbor_NonShortestIntegerNestedInsideASkippedValue_IsRejected()
    {
        // Map {1: 0x1800} — non-shortest zero inside the skipped value.
        var bytes = Convert.FromHexString("a1011800");

        var reader = new CanonicalCborReader(bytes);
        reader.ReadStartMap();
        reader.ReadKey();

        Assert.ThrowsExactly<CborFormatException>(reader.SkipValue);
    }
}
