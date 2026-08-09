using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the 54-byte record header byte-exactly against specification
/// 04 §2–§2.1: round-trip, and a named refusal for every field rule.
/// </summary>
[TestClass]
public sealed class RecordHeaderTests
{
    private static readonly ObjectId SomeId =
        ObjectId.FromBytes(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    private static RecordHeader Sample() => new(
        ObjectType.SegmentRecord,
        CompressionProfile.ZstdV1,
        EncryptionProfile.Aes256GcmV1,
        ordinal: 47,
        logicalLength: 65_536,
        storedLength: 31_000,
        SomeId);

    private static byte[] SampleBytes()
    {
        var bytes = new byte[RecordHeader.Length];
        Sample().WriteTo(bytes);
        return bytes;
    }

    [TestMethod]
    public void RecordHeader_SerialisedThenParsed_RoundTrips()
    {
        var parsed = RecordHeader.Parse(SampleBytes());

        Assert.AreEqual(ObjectType.SegmentRecord, parsed.ObjectType);
        Assert.AreEqual(CompressionProfile.ZstdV1, parsed.CompressionProfile);
        Assert.AreEqual(EncryptionProfile.Aes256GcmV1, parsed.EncryptionProfile);
        Assert.AreEqual(47u, parsed.Ordinal);
        Assert.AreEqual(65_536UL, parsed.LogicalLength);
        Assert.AreEqual(31_000u, parsed.StoredLength);
        Assert.AreEqual(SomeId, parsed.ObjectId);
    }

    [TestMethod]
    public void RecordHeader_ASerialisedHeader_PlacesEveryFieldAtItsSpecifiedOffset()
    {
        var bytes = SampleBytes();

        Assert.AreEqual(0x52, bytes[0]);                                     // marker
        Assert.AreEqual(0x01, bytes[1]);                                     // object_type
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[2..4]);             // compression zstd-v1
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x01 }, bytes[4..6]);             // encryption aes-256-gcm-v1
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x2F }, bytes[6..10]); // ordinal 47
        Assert.AreEqual(0x00, bytes[10]);                                    // logical_length u64 BE
        SequenceAssert.AreEqual(new byte[] { 0x00, 0x00, 0x79, 0x18 }, bytes[18..22]); // stored_length 31000
        SequenceAssert.AreEqual(SomeId.ToArray(), bytes[22..54]);
    }

    [TestMethod]
    public void RecordHeader_MarkerIsWrong_IsRefused()
    {
        var bytes = SampleBytes();
        bytes[0] = 0x00;

        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [TestMethod]
    public void RecordHeader_LogicalLengthIsZero_IsADamageFinding()
    {
        var bytes = SampleBytes();
        bytes.AsSpan(10, 8).Clear();

        var exception = Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.Contains("damage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void RecordHeader_StoredLengthExceedsSixtyFourMebibytes_IsRefusedBeforeAllocation()
    {
        var bytes = SampleBytes();
        bytes[18] = 0xFF;
        bytes[19] = 0xFF;
        bytes[20] = 0xFF;
        bytes[21] = 0xFF;

        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [TestMethod]
    public void RecordHeader_OrdinalExceedsTheMaximum_IsRefused()
    {
        var bytes = SampleBytes();
        bytes[6] = 0x00;
        bytes[7] = 0x01;
        bytes[8] = 0x00;
        bytes[9] = 0x00; // 65 536

        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.ZstdV1, EncryptionProfile.Aes256GcmV1,
            65_536, 100, 50, SomeId));
    }

    [TestMethod]
    public void RecordHeader_ObjectTypeIsTheReservedOne_IsRefused()
    {
        var bytes = SampleBytes();
        bytes[1] = 0x07;

        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [TestMethod]
    public void RecordHeader_ProfileIsUnknown_IsRefusedRatherThanGuessed()
    {
        var unknownCompression = SampleBytes();
        unknownCompression[3] = 0x77;
        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(unknownCompression));

        var unknownEncryption = SampleBytes();
        unknownEncryption[5] = 0x77;
        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(unknownEncryption));
    }

    [TestMethod]
    public void RecordHeader_CompressionIsNoneAndLengthsDisagree_IsRefused()
    {
        var bytes = new byte[RecordHeader.Length];
        new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.None, EncryptionProfile.Aes256GcmV1,
            0, 100, 100, SomeId).WriteTo(bytes);
        bytes[21] = 99; // stored_length 99 != logical_length 100

        Assert.ThrowsExactly<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.None, EncryptionProfile.Aes256GcmV1,
            0, 100, 99, SomeId));
    }
}
