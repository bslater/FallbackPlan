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
    public void Cdc_v1_with_validated_parameters_is_clean()
    {
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = SegmentationProfile.CdcV1,
            CdcParameters = CdcParameters.Default,
        };

        Assert.True(policy.Validate().IsValid);
    }

    [Fact]
    public void Cdc_v1_without_parameters_is_refused_by_name()
    {
        var policy = CapturePolicy.Default with { SegmentationProfile = SegmentationProfile.CdcV1 };

        var result = policy.Validate();

        Assert.False(result.IsValid);
        Assert.True(result.Has("cdc_parameters_missing"));
    }

    [Fact]
    public void Cdc_parameters_under_a_fixed_profile_are_refused_by_name()
    {
        var policy = CapturePolicy.Default with { CdcParameters = CdcParameters.Default };

        Assert.True(policy.Validate().Has("cdc_parameters_without_cdc_profile"));
    }

    [Theory]
    [InlineData(100_000, 12_500, 800_000)]        // target not a power of two
    [InlineData(32 * 1024, 4 * 1024, 256 * 1024)] // target below 64 KiB
    [InlineData(65_536, 8_191, 524_288)]          // min below target/8
    [InlineData(65_536, 8_192, 524_289)]          // max above target*8
    [InlineData(1_048_576, 900_000, 800_000)]     // min above max
    public void Out_of_range_cdc_parameters_cannot_be_constructed(int target, int min, int max)
    {
        Assert.False(CdcParameters.TryCreate(target, min, max, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdcParameters.Create(target, min, max));
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
        Assert.True(result.Defects.Count >= 3);
        Assert.True(result.Has("cdc_parameters_missing"));
        Assert.True(result.Has("compression_level_out_of_range"));
        Assert.True(result.Has("compression_threshold_out_of_range"));
    }
}
