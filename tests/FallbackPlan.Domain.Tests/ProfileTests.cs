using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the profile families (specification 00 §3; NFR-SEC-002): known
/// values resolve to their singletons, unassigned values are refused, and the
/// private-use range is refused because it must not appear in a portable
/// repository.
///
/// Refusing the unassigned and private-use ranges is what NFR-SEC-002 asks for
/// concretely: an unreviewed suite has no way into a repository, because there
/// is no accepted encoding for one.
/// </summary>
public sealed class ProfileTests
{
    [Fact]
    public void Known_segmentation_profiles_resolve()
    {
        Assert.True(SegmentationProfile.TryFromValue(0x0001, out var fixedV1));
        Assert.Same(SegmentationProfile.FixedV1, fixedV1);
        Assert.True(SegmentationProfile.TryFromValue(0x0002, out var cdcV1));
        Assert.Same(SegmentationProfile.CdcV1, cdcV1);
    }

    [Fact]
    public void Known_compression_profiles_resolve()
    {
        Assert.True(CompressionProfile.TryFromValue(0x0000, out var none));
        Assert.Same(CompressionProfile.None, none);
        Assert.True(CompressionProfile.TryFromValue(0x0001, out var zstd));
        Assert.Same(CompressionProfile.ZstdV1, zstd);
    }

    [Fact]
    public void Known_encryption_profiles_resolve()
    {
        Assert.True(EncryptionProfile.TryFromValue(0x0001, out var aes));
        Assert.Same(EncryptionProfile.Aes256GcmV1, aes);

        // 0x0002 is reserved and never assigned: a draft admitted
        // xchacha20-poly1305-v1 there, and it was withdrawn before the freeze
        // because nothing existed to cross-verify it against (03 §6.1). The
        // value must resolve to nothing rather than to a suite, and it must
        // not be handed to some later one.
        Assert.False(EncryptionProfile.TryFromValue(EncryptionProfile.ReservedWithdrawnValue, out var reserved));
        Assert.Null(reserved);
    }

    [Fact]
    public void Known_content_hash_and_kdf_profiles_resolve()
    {
        Assert.True(ContentHashProfile.TryFromValue(0x0001, out var sha256));
        Assert.Same(ContentHashProfile.Sha256V1, sha256);
        Assert.True(KdfProfile.TryFromValue(0x0001, out var argon2id));
        Assert.Same(KdfProfile.Argon2id, argon2id);
    }

    [Theory]
    [InlineData((ushort)0x0003)]
    [InlineData((ushort)0x7FFF)]
    public void Unassigned_values_are_refused(ushort value)
    {
        Assert.False(SegmentationProfile.TryFromValue(value, out _));
        Assert.False(EncryptionProfile.TryFromValue(value, out _));
        Assert.False(ContentHashProfile.TryFromValue(value, out _));
        Assert.False(KdfProfile.TryFromValue(value, out _));
    }

    [Theory]
    [InlineData((ushort)0x8000)]
    [InlineData((ushort)0xFFFF)]
    public void Private_use_range_is_refused_in_a_portable_repository(ushort value)
    {
        Assert.False(SegmentationProfile.TryFromValue(value, out _));
        Assert.False(CompressionProfile.TryFromValue(value, out _));
        Assert.False(EncryptionProfile.TryFromValue(value, out _));
        Assert.False(ContentHashProfile.TryFromValue(value, out _));
        Assert.False(KdfProfile.TryFromValue(value, out _));
    }

    [Fact]
    public void Compression_none_is_a_valid_wire_value_but_segmentation_zero_is_not()
    {
        Assert.True(CompressionProfile.TryFromValue(0x0000, out _));
        Assert.False(SegmentationProfile.TryFromValue(0x0000, out _));
        Assert.False(EncryptionProfile.TryFromValue(0x0000, out _));
    }
}
