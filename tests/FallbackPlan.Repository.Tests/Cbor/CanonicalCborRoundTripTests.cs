using FallbackPlan.TestSupport;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Tests.Cbor;

/// <summary>
/// The round-trip half of A2's acceptance: what the writer produces, the
/// reader accepts, and re-encoding what was read is byte-identical
/// (NFR-PORT-003, NFR-COMP-004).
/// </summary>
[TestClass]
public sealed class CanonicalCborRoundTripTests
{
    [TestMethod]
    public void CanonicalCbor_ANestedDocument_RoundTripsByteIdentically()
    {
        var first = Encode();
        var second = ReencodeByReading(first);

        SequenceAssert.AreEqual(first, second);

        static byte[] Encode()
        {
            var writer = new CanonicalCborWriter();
            writer.WriteStartMap(4);
            writer.WriteKey(1);
            writer.WriteByteString([0xAA, 0xBB]);
            writer.WriteKey(2);
            writer.WriteUnsignedInteger(65_536);
            writer.WriteKey(3);
            writer.WriteStartArray(3);
            writer.WriteTextString("segment");
            writer.WriteBoolean(true);
            writer.WriteSignedInteger(-42);
            writer.WriteEndArray();
            writer.WriteKey(4);
            writer.WriteStartMap(1);
            writer.WriteKey(7);
            writer.WriteUnsignedInteger(0);
            writer.WriteEndMap();
            writer.WriteEndMap();
            return writer.Encode();
        }

        static byte[] ReencodeByReading(byte[] encoded)
        {
            var reader = new CanonicalCborReader(encoded);
            var writer = new CanonicalCborWriter();

            var entries = reader.ReadStartMap();
            writer.WriteStartMap(entries);

            writer.WriteKey(reader.ReadKey());
            writer.WriteByteString(reader.ReadByteString(maxLength: 16));
            writer.WriteKey(reader.ReadKey());
            writer.WriteUnsignedInteger(reader.ReadUnsignedInteger());
            writer.WriteKey(reader.ReadKey());
            var items = reader.ReadStartArray(maxCount: 8);
            writer.WriteStartArray(items);
            writer.WriteTextString(reader.ReadTextString(maxUtf8Length: 64));
            writer.WriteBoolean(reader.ReadBoolean());
            writer.WriteSignedInteger(reader.ReadSignedInteger());
            reader.ReadEndArray();
            writer.WriteEndArray();
            writer.WriteKey(reader.ReadKey());
            var inner = reader.ReadStartMap();
            writer.WriteStartMap(inner);
            writer.WriteKey(reader.ReadKey());
            writer.WriteUnsignedInteger(reader.ReadUnsignedInteger());
            reader.ReadEndMap();
            writer.WriteEndMap();
            reader.ReadEndMap();
            writer.WriteEndMap();

            reader.AssertEndOfDocument();
            return writer.Encode();
        }
    }

    [TestMethod]
    public void CanonicalCborWriter_EntriesWrittenOutOfOrder_EmitsThemInCanonicalOrder()
    {
        var sorted = new CanonicalCborWriter();
        sorted.WriteStartMap(2);
        sorted.WriteKey(1);
        sorted.WriteUnsignedInteger(10);
        sorted.WriteKey(2);
        sorted.WriteUnsignedInteger(20);
        sorted.WriteEndMap();

        var unsorted = new CanonicalCborWriter();
        unsorted.WriteStartMap(2);
        unsorted.WriteKey(2);
        unsorted.WriteUnsignedInteger(20);
        unsorted.WriteKey(1);
        unsorted.WriteUnsignedInteger(10);
        unsorted.WriteEndMap();

        SequenceAssert.AreEqual(sorted.Encode(), unsorted.Encode());
    }

    [TestMethod]
    public void CanonicalCborWriter_AnyDocumentItProduces_PassesTheValidator()
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartArray(2);
        writer.WriteUnsignedInteger(23);
        writer.WriteUnsignedInteger(24);
        writer.WriteEndArray();

        CanonicalCbor.Validate(writer.Encode());
    }

    [TestMethod]
    public void CanonicalCbor_AnIntegerKeyedMap_RoundTripsByteIdentically() =>
        PropertyCheck.Holds(this);

    public static bool CanonicalCbor_AnIntegerKeyedMap_RoundTripsByteIdenticallyProperty((uint Key, ulong Value)[]? entries)
    {
        entries ??= [];
        var distinct = entries
            .DistinctBy(entry => entry.Key)
            .ToArray();

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(distinct.Length);
        foreach (var (key, value) in distinct)
        {
            writer.WriteKey(key);
            writer.WriteUnsignedInteger(value);
        }

        writer.WriteEndMap();
        var encoded = writer.Encode();

        CanonicalCbor.Validate(encoded);

        var reader = new CanonicalCborReader(encoded);
        var reencoder = new CanonicalCborWriter();
        var count = reader.ReadStartMap();
        reencoder.WriteStartMap(count);
        for (var i = 0; i < count; i++)
        {
            reencoder.WriteKey(reader.ReadKey());
            reencoder.WriteUnsignedInteger(reader.ReadUnsignedInteger());
        }

        reader.ReadEndMap();
        reencoder.WriteEndMap();
        reader.AssertEndOfDocument();

        return encoded.AsSpan().SequenceEqual(reencoder.Encode());
    }

    [TestMethod]
    public void CanonicalCbor_AnArrayOfByteStrings_RoundTripsByteIdentically() =>
        PropertyCheck.Holds(this);

    public static bool CanonicalCbor_AnArrayOfByteStrings_RoundTripsByteIdenticallyProperty(byte[][]? strings)
    {
        strings ??= [];
        var items = strings.Select(item => item ?? []).ToArray();

        var writer = new CanonicalCborWriter();
        writer.WriteStartArray(items.Length);
        foreach (var item in items)
        {
            writer.WriteByteString(item);
        }

        writer.WriteEndArray();
        var encoded = writer.Encode();

        CanonicalCbor.Validate(encoded);

        var reader = new CanonicalCborReader(encoded);
        var reencoder = new CanonicalCborWriter();
        var count = reader.ReadStartArray(maxCount: int.MaxValue);
        reencoder.WriteStartArray(count);
        for (var i = 0; i < count; i++)
        {
            reencoder.WriteByteString(reader.ReadByteString(maxLength: int.MaxValue));
        }

        reader.ReadEndArray();
        reencoder.WriteEndArray();
        reader.AssertEndOfDocument();

        return encoded.AsSpan().SequenceEqual(reencoder.Encode());
    }
}
