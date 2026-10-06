using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The source-identity pack codec (specification 06 §11.5, ADR-0090): one
/// publication's hints in one body. It round-trips exactly, encodes to one
/// byte string whatever order its entries arrive in, and the reader refuses
/// an unknown schema, a field of the wrong width, and entries out of order
/// rather than interpreting any of them. A pack that answers wrongly attaches
/// ancestry a manifest keeps forever (FR-MAN-003), so the strictness is the
/// same as the per-file hint's.
/// </summary>
[TestClass]
public sealed class SourceIdentityPackCodecTests
{
    private static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x22, 16)];
    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x7A, 16)];

    private static ObjectId TestObjectId(byte seed)
    {
        var bytes = new byte[ObjectId.Size];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static byte[] SourceKey(byte seed) =>
        [.. Enumerable.Repeat(seed, SourceIdentityHint.SourceKeyLength)];

    private static SourceIdentityPackEntry Entry(byte sourceKey, byte objectId) => new()
    {
        SourceKey = SourceKey(sourceKey),
        ObjectId = TestObjectId(objectId),
    };

    private static SourceIdentityPack Sample(params SourceIdentityPackEntry[] entries) => new()
    {
        DeviceId = DeviceId,
        SnapshotId = SnapshotId,
        CapturedAt = 1_722_600_000_000,
        Part = 0,
        Entries = entries.Length == 0 ? [Entry(0x11, 0xA1), Entry(0x22, 0xB2)] : entries,
    };

    /// <summary>
    /// The sample pack, encoded by hand from the specification's text rather
    /// than by this codec: a six-entry map, then two entries of a 16-byte
    /// source key and a 32-byte object identifier, ascending by source key.
    /// </summary>
    private const string SampleHex =
        "a6"
        + "0101"
        + "0250" + "22222222222222222222222222222222"
        + "0350" + "7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a"
        + "041b0000019112f60a00"
        + "0500"
        + "0682"
        + "8250" + "11111111111111111111111111111111"
        + "5820" + "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"
        + "8250" + "22222222222222222222222222222222"
        + "5820" + "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";

    [TestMethod]
    public void SourceIdentityPack_EncodedAndDecoded_RoundTripsExactly()
    {
        var decoded = SourceIdentityPackCodec.Decode(SourceIdentityPackCodec.Encode(Sample()));

        SequenceAssert.AreEqual(DeviceId, decoded.DeviceId.ToArray());
        SequenceAssert.AreEqual(SnapshotId, decoded.SnapshotId.ToArray());
        Assert.AreEqual(1_722_600_000_000ul, decoded.CapturedAt);
        Assert.AreEqual(0u, decoded.Part);
        Assert.HasCount(2, decoded.Entries);
        Assert.AreEqual(Entry(0x11, 0xA1), decoded.Entries[0]);
        Assert.AreEqual(Entry(0x22, 0xB2), decoded.Entries[1]);
    }

    [TestMethod]
    public void SourceIdentityPack_Encoded_IsTheBodyTheSpecificationDescribes()
    {
        Assert.AreEqual(SampleHex, Convert.ToHexStringLower(SourceIdentityPackCodec.Encode(Sample())));
    }

    [TestMethod]
    public void SourceIdentityPack_EntriesGivenOutOfOrder_EncodeInSourceKeyOrder()
    {
        // One body per set of entries, whatever order the walk met the files
        // in: the encoding is canonical, so two writers of the same facts
        // write the same bytes.
        var encoded = SourceIdentityPackCodec.Encode(Sample(Entry(0x22, 0xB2), Entry(0x11, 0xA1)));

        Assert.AreEqual(SampleHex, Convert.ToHexStringLower(encoded));
    }

    [TestMethod]
    public void SourceIdentityPack_TwoEntriesForOneSourceKey_IsRefused()
    {
        // 06 §11: a writer that sees one source key claimed twice in one
        // snapshot publishes no hint for it rather than choosing.
        var pack = Sample(Entry(0x11, 0xA1), Entry(0x11, 0xB2));

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityPackCodec.Encode(pack));
    }

    [TestMethod]
    public void SourceIdentityPack_WithNoEntries_IsRefused()
    {
        var pack = Sample() with { Entries = [] };

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityPackCodec.Encode(pack));
    }

    [TestMethod]
    public void SourceIdentityPack_WithMoreEntriesThanAPackHolds_IsRefused()
    {
        var entries = Enumerable.Range(0, SourceIdentityPack.MaxEntries + 1)
            .Select(index =>
            {
                var key = new byte[SourceIdentityHint.SourceKeyLength];
                BitConverter.TryWriteBytes(key, index);
                return new SourceIdentityPackEntry { SourceKey = key, ObjectId = TestObjectId(0x01) };
            })
            .ToArray();

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityPackCodec.Encode(Sample(entries)));
    }

    [TestMethod]
    public void SourceIdentityPack_FitsTheMetadataObjectCeiling_AtItsLargest()
    {
        // MaxEntries is chosen so a full pack, sealed, is still a standalone
        // metadata object a reader accepts (FormatLimits.MaxMetadataObjectSize).
        const long EntryBytes = 52;
        Assert.IsLessThan(
            FallbackPlan.Domain.FormatLimits.MaxMetadataObjectSize / 2L,
            SourceIdentityPack.MaxEntries * EntryBytes);
    }

    [TestMethod]
    [DataRow(15)]
    [DataRow(17)]
    public void SourceIdentityPack_ASourceKeyOfTheWrongWidth_IsRefused(int length)
    {
        var pack = Sample(new SourceIdentityPackEntry { SourceKey = new byte[length], ObjectId = TestObjectId(0x01) });

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityPackCodec.Encode(pack));
    }

    [TestMethod]
    public void SourceIdentityPack_ADeviceOrSnapshotIdentifierOfTheWrongWidth_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => SourceIdentityPackCodec.Encode(Sample() with { DeviceId = new byte[15] }));
        Assert.ThrowsExactly<ArgumentException>(
            () => SourceIdentityPackCodec.Encode(Sample() with { SnapshotId = new byte[17] }));
    }

    [TestMethod]
    public void SourceIdentityPack_SchemaVersionIsUnknown_IsRefusedRatherThanGuessed()
    {
        var encoded = SourceIdentityPackCodec.Encode(Sample());

        // Key 1's value is the third byte of the body: map header, key, value.
        encoded[2] = 0x02;

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityPackCodec.Decode(encoded));
    }

    [TestMethod]
    public void SourceIdentityPack_EntriesOutOfOrder_AreRefusedByTheReader()
    {
        // The writer sorts, so a body whose entries descend was not written by
        // a conforming writer; reading it would make "the last entry wins"
        // depend on whoever wrote it.
        var swapped = Convert.FromHexString(
            SampleHex.Replace("11111111111111111111111111111111", "33333333333333333333333333333333", StringComparison.Ordinal));

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityPackCodec.Decode(swapped));
    }

    [TestMethod]
    public void SourceIdentityPack_ARepeatedSourceKey_IsRefusedByTheReader()
    {
        var repeated = Convert.FromHexString(
            SampleHex.Replace("8250" + "22222222222222222222222222222222", "8250" + "11111111111111111111111111111111", StringComparison.Ordinal));

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityPackCodec.Decode(repeated));
    }

    [TestMethod]
    public void SourceIdentityPack_TrailingBytes_AreRefused()
    {
        var encoded = SourceIdentityPackCodec.Encode(Sample());
        var padded = new byte[encoded.Length + 1];
        encoded.CopyTo(padded, 0);

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityPackCodec.Decode(padded));
    }

    [TestMethod]
    public void SourceIdentityPack_AnEmptyEntryList_IsRefusedByTheReader()
    {
        // The same body with its entry array emptied: 0x80 in place of 0x82
        // and the two entries gone.
        var emptied = Convert.FromHexString(SampleHex[..(SampleHex.IndexOf("0682", StringComparison.Ordinal) + 2)] + "80");

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityPackCodec.Decode(emptied));
    }
}
