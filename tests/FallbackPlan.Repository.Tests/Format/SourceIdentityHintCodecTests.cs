using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The source-identity hint codec (specification 06 §11): a hint round-trips
/// exactly, and the reader refuses an unknown schema and a field of the
/// wrong width rather than interpreting either — a hint that answers wrongly
/// attaches ancestry a manifest keeps forever.
/// </summary>
[TestClass]
public sealed class SourceIdentityHintCodecTests
{
    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x7A, 16)];

    private static ObjectId TestObjectId(byte seed)
    {
        var bytes = new byte[ObjectId.Size];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static byte[] SourceKey(byte seed) =>
        [.. Enumerable.Repeat(seed, SourceIdentityHint.SourceKeyLength)];

    private static SourceIdentityHint Sample() => new()
    {
        SourceKey = SourceKey(0xA1),
        SnapshotId = SnapshotId,
        ObjectId = TestObjectId(0x11),
        CapturedAt = 1_722_600_000_000,
    };

    [TestMethod]
    public void SourceIdentityHint_EncodedAndDecoded_RoundTripsExactly()
    {
        var decoded = SourceIdentityHintCodec.Decode(SourceIdentityHintCodec.Encode(Sample()));

        Assert.AreEqual(Sample(), decoded);
        SequenceAssert.AreEqual(SourceKey(0xA1), decoded.SourceKey.ToArray());
        SequenceAssert.AreEqual(SnapshotId, decoded.SnapshotId.ToArray());
    }

    [TestMethod]
    public void SourceIdentityHint_EncodedTwice_ProducesIdenticalBytes()
    {
        SequenceAssert.AreEqual(SourceIdentityHintCodec.Encode(Sample()), SourceIdentityHintCodec.Encode(Sample()));
    }

    [TestMethod]
    [DataRow(15)]
    [DataRow(17)]
    public void SourceIdentityHint_SourceKeyIsTheWrongWidth_IsRefused(int length)
    {
        var hint = Sample() with { SourceKey = new byte[length] };

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityHintCodec.Encode(hint));
    }

    [TestMethod]
    public void SourceIdentityHint_SnapshotIdentifierIsTheWrongWidth_IsRefused()
    {
        var hint = Sample() with { SnapshotId = new byte[15] };

        Assert.ThrowsExactly<ArgumentException>(() => SourceIdentityHintCodec.Encode(hint));
    }

    [TestMethod]
    public void SourceIdentityHint_SchemaVersionIsUnknown_IsRefusedRatherThanGuessed()
    {
        var encoded = SourceIdentityHintCodec.Encode(Sample());

        // Key 1's value is the third byte of the body: map header, key, value.
        encoded[2] = 0x09;

        Assert.ThrowsExactly<ManifestValidationException>(() => SourceIdentityHintCodec.Decode(encoded));
    }

    [TestMethod]
    public void SourceIdentityHint_TrailingBytesFollowTheDocument_AreRefused()
    {
        var encoded = SourceIdentityHintCodec.Encode(Sample());

        Assert.ThrowsExactly<ManifestValidationException>(
            () => SourceIdentityHintCodec.Decode((byte[])[.. encoded, 0x00]));
    }
}
