using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// Exercises capture-policy validation (specification 06 §7; FR-ARCH-007,
/// NFR-OPS-003): the default is clean, every defect carries its stable name,
/// and defects aggregate rather than stopping at the first.
/// </summary>
[TestClass]
public sealed class CapturePolicyValidationTests
{
    [TestMethod]
    public void The_default_policy_validates_clean()
    {
        Assert.IsTrue(CapturePolicy.Default.Validate().IsValid);
    }

    [TestMethod]
    public void Cdc_v1_with_validated_parameters_is_clean()
    {
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = SegmentationProfile.CdcV1,
            CdcParameters = CdcParameters.Default,
        };

        Assert.IsTrue(policy.Validate().IsValid);
    }

    [TestMethod]
    public void Cdc_v1_without_parameters_is_refused_by_name()
    {
        var policy = CapturePolicy.Default with { SegmentationProfile = SegmentationProfile.CdcV1 };

        var result = policy.Validate();

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Has("cdc_parameters_missing"));
    }

    [TestMethod]
    public void Cdc_parameters_under_a_fixed_profile_are_refused_by_name()
    {
        var policy = CapturePolicy.Default with { CdcParameters = CdcParameters.Default };

        Assert.IsTrue(policy.Validate().Has("cdc_parameters_without_cdc_profile"));
    }

    [TestMethod]
    [DataRow(100_000, 12_500, 800_000)]        // target not a power of two
    [DataRow(32 * 1024, 4 * 1024, 256 * 1024)] // target below 64 KiB
    [DataRow(65_536, 8_191, 524_288)]          // min below target/8
    [DataRow(65_536, 8_192, 524_289)]          // max above target*8
    [DataRow(1_048_576, 900_000, 800_000)]     // min above max
    public void Out_of_range_cdc_parameters_cannot_be_constructed(int target, int min, int max)
    {
        Assert.IsFalse(CdcParameters.TryCreate(target, min, max, out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CdcParameters.Create(target, min, max));
    }

    [TestMethod]
    public void An_unset_segment_size_is_named()
    {
        var policy = CapturePolicy.Default with { SegmentSize = default };

        Assert.IsTrue(policy.Validate().Has("segment_size_out_of_range"));
    }

    [TestMethod]
    public void A_compression_level_out_of_range_is_named()
    {
        var policy = CapturePolicy.Default with
        {
            Compression = CompressionSettings.Default with { ZstdLevel = 20 },
        };

        Assert.IsTrue(policy.Validate().Has("compression_level_out_of_range"));
    }

    [TestMethod]
    public void A_compression_threshold_out_of_range_is_named()
    {
        var policy = CapturePolicy.Default with
        {
            Compression = CompressionSettings.Default with { ThresholdPermille = 1001 },
        };

        Assert.IsTrue(policy.Validate().Has("compression_threshold_out_of_range"));
    }

    [TestMethod]
    public void An_unknown_trust_domain_is_named()
    {
        var policy = CapturePolicy.Default with { DedupTrustDomain = (DedupTrustDomain)9 };

        Assert.IsTrue(policy.Validate().Has("dedup_trust_domain_unknown"));
    }

    [TestMethod]
    public void Multiple_defects_are_reported_together()
    {
        var policy = CapturePolicy.Default with
        {
            SegmentationProfile = SegmentationProfile.CdcV1,
            SegmentSize = default,
            Compression = CompressionSettings.Default with { ZstdLevel = 0, ThresholdPermille = 2000 },
        };

        var result = policy.Validate();

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Defects.Count >= 3);
        Assert.IsTrue(result.Has("cdc_parameters_missing"));
        Assert.IsTrue(result.Has("compression_level_out_of_range"));
        Assert.IsTrue(result.Has("compression_threshold_out_of_range"));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(CapturePolicy.MaximumConcurrency + 1)]
    public void A_concurrency_outside_the_bound_is_a_named_defect(int concurrency)
    {
        // Memory is bounded by concurrency × segment size (NFR-PERF-001), so an
        // unbounded setting does not make the pipeline faster — it makes the
        // bound unstatable.
        var result = (CapturePolicy.Default with { Concurrency = concurrency }).Validate();

        Assert.IsFalse(result.IsValid);
        Assert.Contains(defect => defect.Name == "concurrency_out_of_range", result.Defects);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(CapturePolicy.DefaultConcurrency)]
    [DataRow(CapturePolicy.MaximumConcurrency)]
    public void Concurrency_within_the_bound_is_accepted(int concurrency)
    {
        // 1 in particular must stay valid: it is the configuration in which the
        // ordering barrier is trivially satisfied, and the control case for
        // NFR-PERF-002's acceptance test.
        Assert.IsTrue((CapturePolicy.Default with { Concurrency = concurrency }).Validate().IsValid);
    }

    [TestMethod]
    public void The_default_is_below_the_machines_capacity_on_purpose()
    {
        // NFR-OPS-004: defaults safe on a 4-core laptop. A backup that makes the
        // machine unpleasant to use gets switched off, and a switched-off backup
        // protects nothing.
        Assert.AreEqual(CapturePolicy.DefaultConcurrency, CapturePolicy.Default.Concurrency);
        Assert.IsTrue(CapturePolicy.DefaultConcurrency < 4);
    }

}
