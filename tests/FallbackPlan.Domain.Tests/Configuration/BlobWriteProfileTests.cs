using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// Exercises blob-write-profile validation (specification 05 §5, 00 §8) —
/// including A5's acceptance criterion: a profile exceeding a provider limit
/// is rejected at configuration time with a named reason (FR-ARCH-007).
/// </summary>
[TestClass]
public sealed class BlobWriteProfileTests
{
    [TestMethod]
    public void The_specification_defaults_validate_clean()
    {
        Assert.IsTrue(BlobWriteProfile.LocalDefault.Validate().IsValid);
        Assert.IsTrue(BlobWriteProfile.ObjectStoreDefault.Validate().IsValid);
    }

    [TestMethod]
    public void A_profile_exceeding_a_provider_limit_is_rejected_at_configuration_time_with_a_named_reason()
    {
        // The acceptance scenario: the object-store default seals up to
        // 512 MiB, but this provider only accepts 256 MiB objects.
        var result = BlobWriteProfile.ObjectStoreDefault.ValidateAgainstStore(256L * 1024 * 1024);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Has("blob_maximum_exceeds_provider_object_size"));
    }

    [TestMethod]
    public void A_profile_within_the_provider_limit_passes_the_store_check()
    {
        Assert.IsTrue(BlobWriteProfile.LocalDefault.ValidateAgainstStore(long.MaxValue).IsValid);
    }

    [TestMethod]
    public void A_maximum_above_the_format_blob_limit_is_named()
    {
        var profile = BlobWriteProfile.ObjectStoreDefault with { MaximumSizeBytes = 513L * 1024 * 1024 };

        Assert.IsTrue(profile.Validate().Has("blob_maximum_exceeds_format_limit"));
    }

    [TestMethod]
    public void A_target_above_the_maximum_is_named()
    {
        var profile = BlobWriteProfile.LocalDefault with { TargetSizeBytes = 512L * 1024 * 1024 };

        Assert.IsTrue(profile.Validate().Has("blob_target_exceeds_maximum"));
    }

    [TestMethod]
    public void A_record_count_above_the_format_limit_is_named()
    {
        var profile = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 65_537 };

        Assert.IsTrue(profile.Validate().Has("blob_record_count_exceeds_format_limit"));
    }

    [TestMethod]
    public void The_policy_level_store_check_aggregates_both_sources()
    {
        var policy = CapturePolicy.Default with
        {
            BlobWriteProfile = BlobWriteProfile.ObjectStoreDefault,
            Compression = CompressionSettings.Default with { ZstdLevel = 0 },
        };

        var result = policy.ValidateAgainstStore(256L * 1024 * 1024);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Has("compression_level_out_of_range"));
        Assert.IsTrue(result.Has("blob_maximum_exceeds_provider_object_size"));
    }
}
