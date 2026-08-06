using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Format.Records;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// Exercises the 54-byte record header byte-exactly against specification
/// 04 §2–§2.1: round-trip, and a named refusal for every field rule.
/// </summary>
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

    [Fact]
    public void Serialize_then_parse_round_trips()
    {
        var parsed = RecordHeader.Parse(SampleBytes());

        Assert.Equal(ObjectType.SegmentRecord, parsed.ObjectType);
        Assert.Equal(CompressionProfile.ZstdV1, parsed.CompressionProfile);
        Assert.Equal(EncryptionProfile.Aes256GcmV1, parsed.EncryptionProfile);
        Assert.Equal(47u, parsed.Ordinal);
        Assert.Equal(65_536UL, parsed.LogicalLength);
        Assert.Equal(31_000u, parsed.StoredLength);
        Assert.Equal(SomeId, parsed.ObjectId);
    }

    [Fact]
    public void Field_offsets_are_byte_exact()
    {
        var bytes = SampleBytes();

        Assert.Equal(0x52, bytes[0]);                                     // marker
        Assert.Equal(0x01, bytes[1]);                                     // object_type
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[2..4]);             // compression zstd-v1
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes[4..6]);             // encryption aes-256-gcm-v1
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x2F }, bytes[6..10]); // ordinal 47
        Assert.Equal(0x00, bytes[10]);                                    // logical_length u64 BE
        Assert.Equal(new byte[] { 0x00, 0x00, 0x79, 0x18 }, bytes[18..22]); // stored_length 31000
        Assert.Equal(SomeId.ToArray(), bytes[22..54]);
    }

    [Fact]
    public void A_bad_marker_is_refused()
    {
        var bytes = SampleBytes();
        bytes[0] = 0x00;

        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [Fact]
    public void Logical_length_zero_is_a_damage_finding()
    {
        var bytes = SampleBytes();
        bytes.AsSpan(10, 8).Clear();

        var exception = Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.Contains("damage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stored_length_over_64_mib_is_refused_before_allocation()
    {
        var bytes = SampleBytes();
        bytes[18] = 0xFF;
        bytes[19] = 0xFF;
        bytes[20] = 0xFF;
        bytes[21] = 0xFF;

        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [Fact]
    public void An_ordinal_above_65535_is_refused()
    {
        var bytes = SampleBytes();
        bytes[6] = 0x00;
        bytes[7] = 0x01;
        bytes[8] = 0x00;
        bytes[9] = 0x00; // 65 536

        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.ZstdV1, EncryptionProfile.Aes256GcmV1,
            65_536, 100, 50, SomeId));
    }

    [Fact]
    public void The_reserved_object_type_is_refused()
    {
        var bytes = SampleBytes();
        bytes[1] = 0x07;

        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
    }

    [Fact]
    public void Unknown_profiles_are_refused_not_guessed()
    {
        var unknownCompression = SampleBytes();
        unknownCompression[3] = 0x77;
        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(unknownCompression));

        var unknownEncryption = SampleBytes();
        unknownEncryption[5] = 0x77;
        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(unknownEncryption));
    }

    [Fact]
    public void Profile_none_with_mismatched_lengths_is_refused()
    {
        var bytes = new byte[RecordHeader.Length];
        new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.None, EncryptionProfile.Aes256GcmV1,
            0, 100, 100, SomeId).WriteTo(bytes);
        bytes[21] = 99; // stored_length 99 != logical_length 100

        Assert.Throws<RecordFormatException>(() => RecordHeader.Parse(bytes));
        Assert.Throws<ArgumentException>(() => new RecordHeader(
            ObjectType.SegmentRecord, CompressionProfile.None, EncryptionProfile.Aes256GcmV1,
            0, 100, 99, SomeId));
    }
}
