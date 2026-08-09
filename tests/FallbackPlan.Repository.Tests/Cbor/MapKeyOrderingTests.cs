using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// Documents that for shortest-form unsigned-integer map keys — the only keys
/// the format permits (specification 00 §4.2) — CTAP2's length-first ordering
/// and the specification's encoded-byte lexicographic ordering coincide, so a
/// single enforcement layer satisfies rule 3 of 00 §4.1.
/// </summary>
[TestClass]
public sealed class MapKeyOrderingTests
{
    // Keys 0, 1, 23, 24, 255, 256, 65536: their canonical encodings straddle
    // every argument-width boundary. Numeric order, length-first order, and
    // encoded-byte lexicographic order are all the same sequence.
    private const string SortedMap = "a700000100170018180018ff00190100001a0001000000";

    [TestMethod]
    public void MapKeyOrdering_KeysInEncodedByteOrder_AreAccepted()
    {
        CanonicalCbor.Validate(Convert.FromHexString(SortedMap));
    }

    [TestMethod]
    [DataRow("a700000100181800170018ff00190100001a0001000000")] // 24 before 23
    [DataRow("a70000010017001818001901000018ff001a0001000000")] // 256 before 255
    public void MapKeyOrdering_TransposedKeys_AreRejected(string hex)
    {
        Assert.ThrowsExactly<CborFormatException>(() => CanonicalCbor.Validate(Convert.FromHexString(hex)));
    }

    [TestMethod]
    public void CanonicalCborWriter_AnyKeySet_ProducesExactlyTheDocumentedOrder()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(7);
        foreach (var key in new uint[] { 65_536, 256, 255, 24, 23, 1, 0 })
        {
            writer.WriteKey(key);
            writer.WriteUnsignedInteger(0);
        }

        writer.WriteEndMap();

        Assert.AreEqual(SortedMap, Convert.ToHexStringLower(writer.Encode()));
    }
}
