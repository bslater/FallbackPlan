using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// Exercises the recovery footer's record-table codec and its damage-finding
/// constraints (specification 05 §3.1).
/// </summary>
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

    [Fact]
    public void The_record_table_round_trips()
    {
        var entries = new[] { Entry(0, 88), Entry(1, 88 + 54 + 100 + 16) };

        var encoded = BlobFooter.EncodeRecordTable(entries);
        var decoded = BlobFooter.DecodeRecordTable(encoded, 2, blobLength: 100_000);

        Assert.Equal(entries, decoded);
    }

    [Fact]
    public void A_count_mismatch_is_refused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88)]);

        Assert.Throws<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [Fact]
    public void Out_of_order_ordinals_are_refused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(1, 88), Entry(0, 500)]);

        Assert.Throws<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [Fact]
    public void Overlapping_offsets_are_refused()
    {
        // Record 1 starts before record 0 ends.
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88), Entry(1, 100)]);

        Assert.Throws<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 2, 100_000));
    }

    [Fact]
    public void An_entry_extending_past_the_blob_is_refused()
    {
        var encoded = BlobFooter.EncodeRecordTable([Entry(0, 88, stored: 10_000)]);

        Assert.Throws<BlobFormatException>(() => BlobFooter.DecodeRecordTable(encoded, 1, blobLength: 5_000));
    }

    [Fact]
    public void A_declared_table_over_the_metadata_limit_is_refused_before_allocation()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader(1, cborLength: (uint)FormatLimits.MaxMetadataObjectSize + 1, header);

        Assert.Throws<BlobFormatException>(() => BlobFooter.ParseHeader(header));
    }

    [Fact]
    public void A_record_count_over_the_limit_is_refused()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader((uint)FormatLimits.MaxRecordsPerBlob + 1, 100, header);

        Assert.Throws<BlobFormatException>(() => BlobFooter.ParseHeader(header));
    }

    [Fact]
    public void An_absent_footer_magic_names_the_locator_as_the_suspect()
    {
        var header = new byte[BlobFooter.HeaderLength];
        BlobFooter.WriteHeader(1, 100, header);
        header[0] = 0x00;

        var exception = Assert.Throws<BlobFormatException>(() => BlobFooter.ParseHeader(header));
        Assert.Contains("locator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
