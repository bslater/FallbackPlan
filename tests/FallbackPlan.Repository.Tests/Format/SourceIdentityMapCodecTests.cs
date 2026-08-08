using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The source-identity map codec (specification 06 §11): round-trips are
/// exact and canonically ordered, and the reader refuses a map whose
/// entries are out of order, repeated, or of an unknown schema — because a
/// hint that answers ambiguously would attach ancestry a manifest keeps
/// forever.
/// </summary>
public sealed class SourceIdentityMapCodecTests
{
    private static readonly byte[] SnapshotId = [.. Enumerable.Repeat((byte)0x7A, 16)];

    private static ObjectId TestObjectId(byte seed)
    {
        var bytes = new byte[ObjectId.Size];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static byte[] SourceKey(byte seed) =>
        [.. Enumerable.Repeat(seed, SourceIdentityMapCodec.SourceKeyLength)];

    [Fact]
    public void A_map_round_trips_exactly()
    {
        var map = new SourceIdentityMap
        {
            SnapshotId = SnapshotId,
            Identities =
            [
                new SourceIdentity(TestObjectId(0x11), SourceKey(0xA1)),
                new SourceIdentity(TestObjectId(0x22), SourceKey(0xA2)),
            ],
        };

        var decoded = SourceIdentityMapCodec.Decode(SourceIdentityMapCodec.Encode(map));

        Assert.Equal(SnapshotId, decoded.SnapshotId.ToArray());
        Assert.Equal(map.Identities, decoded.Identities);
    }

    [Fact]
    public void The_encoder_sorts_by_object_identifier_so_the_same_capture_is_the_same_bytes()
    {
        var ascending = new SourceIdentityMap
        {
            SnapshotId = SnapshotId,
            Identities =
            [
                new SourceIdentity(TestObjectId(0x11), SourceKey(0xA1)),
                new SourceIdentity(TestObjectId(0x22), SourceKey(0xA2)),
            ],
        };

        var descending = ascending with { Identities = [.. ascending.Identities.Reverse()] };

        Assert.Equal(SourceIdentityMapCodec.Encode(ascending), SourceIdentityMapCodec.Encode(descending));
    }

    [Fact]
    public void An_object_identifier_claimed_by_two_inodes_is_dropped_rather_than_guessed()
    {
        // One file-version manifest, two source files: a manifest is
        // identified by its own bytes, so two identical files in different
        // directories are one object. It cannot say which inode it came from.
        var shared = TestObjectId(0x33);

        var map = SourceIdentityMap.Create(
            SnapshotId,
            [
                new SourceIdentity(shared, SourceKey(0xB1)),
                new SourceIdentity(shared, SourceKey(0xB2)),
                new SourceIdentity(TestObjectId(0x44), SourceKey(0xB3)),
                new SourceIdentity(TestObjectId(0x44), SourceKey(0xB3)),
            ]);

        var identity = Assert.Single(map.Identities);
        Assert.Equal(TestObjectId(0x44), identity.ObjectId);
        Assert.Equal(SourceKey(0xB3), identity.SourceKey.ToArray());
    }

    [Fact]
    public void The_encoder_refuses_a_repeated_object_identifier()
    {
        var map = new SourceIdentityMap
        {
            SnapshotId = SnapshotId,
            Identities =
            [
                new SourceIdentity(TestObjectId(0x55), SourceKey(0xC1)),
                new SourceIdentity(TestObjectId(0x55), SourceKey(0xC2)),
            ],
        };

        Assert.Throws<ArgumentException>(() => SourceIdentityMapCodec.Encode(map));
    }

    [Fact]
    public void A_map_whose_entries_are_out_of_order_is_refused()
    {
        var encoded = SourceIdentityMapCodec.Encode(new SourceIdentityMap
        {
            SnapshotId = SnapshotId,
            Identities =
            [
                new SourceIdentity(TestObjectId(0x11), SourceKey(0xA1)),
                new SourceIdentity(TestObjectId(0x22), SourceKey(0xA2)),
            ],
        });

        // Swap the two object identifiers in place, leaving canonical CBOR
        // that is nonetheless not sorted.
        var start = Array.IndexOf(encoded, (byte)0x11);
        var second = Array.IndexOf(encoded, (byte)0x22);
        Assert.True(start > 0 && second > start);
        encoded.AsSpan(start, ObjectId.Size).Fill(0x22);
        encoded.AsSpan(second, ObjectId.Size).Fill(0x11);

        Assert.Throws<ManifestValidationException>(() => SourceIdentityMapCodec.Decode(encoded));
    }

    [Fact]
    public void An_unknown_schema_version_is_refused_rather_than_guessed()
    {
        var encoded = SourceIdentityMapCodec.Encode(new SourceIdentityMap
        {
            SnapshotId = SnapshotId,
            Identities = [new SourceIdentity(TestObjectId(0x11), SourceKey(0xA1))],
        });

        // Key 1's value is the second byte of the body: map header, key, value.
        encoded[2] = 0x09;

        Assert.Throws<ManifestValidationException>(() => SourceIdentityMapCodec.Decode(encoded));
    }
}
