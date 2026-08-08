using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The source-identity hint codec (specification 06 §11): a hint round-trips
/// exactly, and the reader refuses an unknown schema and a field of the
/// wrong width rather than interpreting either — a hint that answers wrongly
/// attaches ancestry a manifest keeps forever.
/// </summary>
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

    [Fact]
    public void A_hint_round_trips_exactly()
    {
        var decoded = SourceIdentityHintCodec.Decode(SourceIdentityHintCodec.Encode(Sample()));

        Assert.Equal(Sample(), decoded);
        Assert.Equal(SourceKey(0xA1), decoded.SourceKey.ToArray());
        Assert.Equal(SnapshotId, decoded.SnapshotId.ToArray());
    }

    [Fact]
    public void The_same_hint_always_encodes_to_the_same_bytes()
    {
        Assert.Equal(SourceIdentityHintCodec.Encode(Sample()), SourceIdentityHintCodec.Encode(Sample()));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void A_source_key_of_the_wrong_width_is_refused(int length)
    {
        var hint = Sample() with { SourceKey = new byte[length] };

        Assert.Throws<ArgumentException>(() => SourceIdentityHintCodec.Encode(hint));
    }

    [Fact]
    public void A_snapshot_identifier_of_the_wrong_width_is_refused()
    {
        var hint = Sample() with { SnapshotId = new byte[15] };

        Assert.Throws<ArgumentException>(() => SourceIdentityHintCodec.Encode(hint));
    }

    [Fact]
    public void An_unknown_schema_version_is_refused_rather_than_guessed()
    {
        var encoded = SourceIdentityHintCodec.Encode(Sample());

        // Key 1's value is the third byte of the body: map header, key, value.
        encoded[2] = 0x09;

        Assert.Throws<ManifestValidationException>(() => SourceIdentityHintCodec.Decode(encoded));
    }

    [Fact]
    public void Trailing_bytes_are_refused()
    {
        var encoded = SourceIdentityHintCodec.Encode(Sample());

        Assert.Throws<ManifestValidationException>(
            () => SourceIdentityHintCodec.Decode((byte[])[.. encoded, 0x00]));
    }
}
