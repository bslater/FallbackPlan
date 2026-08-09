using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the recovery footer's record-table codec and its damage-finding
/// constraints (specification 05 §3.1).
/// </summary>
[TestClass]
public sealed class BlobFooterTests
{
    private static RecordTableEntry Entry(uint ordinal, ulong offset, uint stored = 100) => new(
        ObjectId.FromBytes(Enumerable.Repeat((byte)ordinal, 32).ToArray()),
        ordinal,
        offset,
        stored,
        LogicalLength: 200,
        CompressionProfileValue: 0x0001,
        EncryptionProfileValue: 0x0001,
        ObjectType.SegmentRecord);

    [TestMethod]
    public void RecordTable_EncodedAndDecoded_RoundTrips()
    {
        var entries = new[] { Entry(0, 88), Entry(1, 88 + 54 + 100 + 16) };

        var encoded = BlobFooter.EncodeRecordTable(entries);
        var decoded = BlobFooter.DecodeRecordTable(encoded, 2, blobLength: 100_000);

        SequenceAssert.AreEqual(entries, decoded);
    }

    [TestMethod]
    public void RecordTable_DeclaredCountDisagreesWithTheEntries_IsRefused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88)]);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [TestMethod]
    public void RecordTable_OrdinalsAreOutOfOrder_IsRefused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(1, 88), Entry(0, 500)]);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [TestMethod]
    public void RecordTable_EntryOffsetsOverlap_IsRefused()
    {
        // Record 1 starts before record 0 ends.
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88), Entry(1, 100)]);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [TestMethod]
    public void RecordTable_AnEntryExtendsPastTheBlob_IsRefused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88, stored: 10_000)]);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 1, blobLength: 5_000));
    }

    [TestMethod]
    public void RecordTable_DeclaredSizeExceedsTheMetadataLimit_IsRefusedBeforeAllocation()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader(1, cborLength: (uint)FormatLimits.MaxMetadataObjectSize + 1, header);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.ParseHeader(header));
    }

    [TestMethod]
    public void RecordTable_RecordCountExceedsTheLimit_IsRefused()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader((uint)FormatLimits.MaxRecordsPerBlob + 1, 100, header);

        Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.ParseHeader(header));
    }

    [TestMethod]
    public void BlobFooter_MagicIsAbsent_NamesTheLocatorAsTheSuspect()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader(1, 100, header);
        header[0] = 0x00;

        var exception = Assert.ThrowsExactly<BlobFormatException>(() => BlobFooter.ParseHeader(header));
        Assert.Contains("locator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
