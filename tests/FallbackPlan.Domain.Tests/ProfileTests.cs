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
[TestClass]
public sealed class ProfileTests
{
    [TestMethod]
    public void Known_segmentation_profiles_resolve()
    {
        Assert.IsTrue(SegmentationProfile.TryFromValue(0x0001, out var fixedV1));
        Assert.AreSame(SegmentationProfile.FixedV1, fixedV1);
        Assert.IsTrue(SegmentationProfile.TryFromValue(0x0002, out var cdcV1));
        Assert.AreSame(SegmentationProfile.CdcV1, cdcV1);
    }

    [TestMethod]
    public void Known_compression_profiles_resolve()
    {
        Assert.IsTrue(CompressionProfile.TryFromValue(0x0000, out var none));
        Assert.AreSame(CompressionProfile.None, none);
        Assert.IsTrue(CompressionProfile.TryFromValue(0x0001, out var zstd));
        Assert.AreSame(CompressionProfile.ZstdV1, zstd);
    }

    [TestMethod]
    public void Known_encryption_profiles_resolve()
    {
        Assert.IsTrue(EncryptionProfile.TryFromValue(0x0001, out var aes));
        Assert.AreSame(EncryptionProfile.Aes256GcmV1, aes);

        // 0x0002 is reserved and never assigned: a draft admitted
        // xchacha20-poly1305-v1 there, and it was withdrawn before the freeze
        // because nothing existed to cross-verify it against (03 §6.1). The
        // value must resolve to nothing rather than to a suite, and it must
        // not be handed to some later one.
        Assert.IsFalse(EncryptionProfile.TryFromValue(EncryptionProfile.ReservedWithdrawnValue, out var reserved));
        Assert.IsNull(reserved);
    }

    [TestMethod]
    public void Known_content_hash_and_kdf_profiles_resolve()
    {
        Assert.IsTrue(ContentHashProfile.TryFromValue(0x0001, out var sha256));
        Assert.AreSame(ContentHashProfile.Sha256V1, sha256);
        Assert.IsTrue(KdfProfile.TryFromValue(0x0001, out var argon2id));
        Assert.AreSame(KdfProfile.Argon2id, argon2id);
    }

    [TestMethod]
    [DataRow((ushort)0x0003)]
    [DataRow((ushort)0x7FFF)]
    public void Unassigned_values_are_refused(ushort value)
    {
        Assert.IsFalse(SegmentationProfile.TryFromValue(value, out _));
        Assert.IsFalse(EncryptionProfile.TryFromValue(value, out _));
        Assert.IsFalse(ContentHashProfile.TryFromValue(value, out _));
        Assert.IsFalse(KdfProfile.TryFromValue(value, out _));
    }

    [TestMethod]
    [DataRow((ushort)0x8000)]
    [DataRow((ushort)0xFFFF)]
    public void Private_use_range_is_refused_in_a_portable_repository(ushort value)
    {
        Assert.IsFalse(SegmentationProfile.TryFromValue(value, out _));
        Assert.IsFalse(CompressionProfile.TryFromValue(value, out _));
        Assert.IsFalse(EncryptionProfile.TryFromValue(value, out _));
        Assert.IsFalse(ContentHashProfile.TryFromValue(value, out _));
        Assert.IsFalse(KdfProfile.TryFromValue(value, out _));
    }

    [TestMethod]
    public void Compression_none_is_a_valid_wire_value_but_segmentation_zero_is_not()
    {
        Assert.IsTrue(CompressionProfile.TryFromValue(0x0000, out _));
        Assert.IsFalse(SegmentationProfile.TryFromValue(0x0000, out _));
        Assert.IsFalse(EncryptionProfile.TryFromValue(0x0000, out _));
    }
}
