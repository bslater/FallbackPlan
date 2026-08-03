using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// Exercises capture-policy validation (specification 06 §7; FR-ARCH-007,
/// NFR-OPS-003): the default is clean, every defect carries its stable name,
/// and defects aggregate rather than stopping at the first.
/// </summary>
public sealed class CapturePolicyValidationTests
{
    [Fact]
    public void The_default_policy_validates_clean()
    {
        Assert.True(CapturePolicy.Default.Validate().IsValid);
    }

    [Fact]
    public void Cdc_v1_is_refused_until_its_parameters_are_pinned()
    {
        var policy = CapturePolicy.Default with { SegmentationProfile = SegmentationProfile.CdcV1 };

        var result = policy.Validate();

        Assert.False(result.IsValid);
        Assert.True(result.Has("segmentation_cdc_parameters_not_pinned"));
    }

    [Fact]
    public void An_unset_segment_size_is_named()
    {
        var policy = CapturePolicy.Default with { SegmentSize = default };

        Assert.True(policy.Validate().Has("segment_size_out_of_range"));
    }

    [Fact]
    public void A_compression_level_out_of_range_is_named()
    {
        var policy = CapturePolicy.Default with
        {
            Compression = CompressionSettings.Default with { ZstdLevel = 20 },
        };

        Assert.True(policy.Validate().Has("compression_level_out_of_range"));
    }

    [Fact]
    public void A_compression_threshold_out_of_range_is_named()
    {
        var policy = CapturePolicy.Default with
        {
            Compression = CompressionSettings.Default with { ThresholdPermille = 1001 },
        };

        Assert.True(policy.Validate().Has("compression_threshold_out_of_range"));
    }

    [Fact]
    public void An_unknown_trust_domain_is_named()
    {
        var policy = CapturePolicy.Default with { DedupTrustDomain = (DedupTrustDomain)9 };

        Assert.True(policy.Validate().Has("dedup_trust_domain_unknown"));
    }

    [Fact]
    public void Multiple_defects_are_reported_together()
    {
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = SegmentationProfile.CdcV1,
            SegmentSize = default,
            Compression = CompressionSettings.Default with { ZstdLevel = 0, ThresholdPermille = 2000 },
        };

        var result = policy.Validate();

        Assert.False(result.IsValid);
        Assert.True(result.Defects.Count >= 4);
        Assert.True(result.Has("segmentation_cdc_parameters_not_pinned"));
        Assert.True(result.Has("segment_size_out_of_range"));
        Assert.True(result.Has("compression_level_out_of_range"));
        Assert.True(result.Has("compression_threshold_out_of_range"));
    }
}
