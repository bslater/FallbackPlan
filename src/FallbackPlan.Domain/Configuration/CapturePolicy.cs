using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// The validated capture policy — the configuration side of the policy
/// manifest (specification 06 §7), checked before use rather than at write
/// time (FR-ARCH-007; NFR-OPS-003). Validation aggregates every defect; it
/// never stops at the first.
/// </summary>
/// <remarks>
/// The encryption profile is drawn from the approved-suite singletons, so an
/// unapproved suite cannot even be expressed — the specification's
/// configuration-time rejection (03 §6) starts at the type level.
/// </remarks>
public sealed record CapturePolicy
{
    /// <summary>The current schema version of this policy shape.</summary>
    public const ushort SchemaVersion = 1;

    /// <summary>The specification defaults: fixed-v1 at 1 MiB, zstd level 3 at 5%, AES-256-GCM, local blob targets.</summary>
    public static readonly CapturePolicy Default = new()
    {
        SegmentationProfile = SegmentationProfile.FixedV1,
        SegmentSize = SegmentSize.Default,
        Compression = CompressionSettings.Default,
        EncryptionProfile = EncryptionProfile.Aes256GcmV1,
        BlobWriteProfile = BlobWriteProfile.LocalDefault,
        DedupTrustDomain = DedupTrustDomain.Repository,
    };

    /// <summary>The segmentation profile.</summary>
    public required SegmentationProfile SegmentationProfile { get; init; }

    /// <summary>The fixed-v1 segment size.</summary>
    public required SegmentSize SegmentSize { get; init; }

    /// <summary>Compression policy.</summary>
    public required CompressionSettings Compression { get; init; }

    /// <summary>The AEAD suite for records, from the approved set only.</summary>
    public required EncryptionProfile EncryptionProfile { get; init; }

    /// <summary>Blob sealing targets.</summary>
    public required BlobWriteProfile BlobWriteProfile { get; init; }

    /// <summary>The segment-reuse trust domain.</summary>
    public required DedupTrustDomain DedupTrustDomain { get; init; }

    /// <summary>
    /// Validates the whole policy, aggregating every named defect from every
    /// section.
    /// </summary>
    public ConfigurationValidationResult Validate()
    {
        List<ConfigurationDefect>? defects = null;

        if (SegmentationProfile == SegmentationProfile.CdcV1)
        {
            // The rolling-hash parameters are not yet pinned; until they are, a
            // portable repository must not be written with cdc-v1 (09 §3.1).
            (defects ??= []).Add(new ConfigurationDefect(
                "segmentation_cdc_parameters_not_pinned",
                "cdc-v1 cannot be used yet: its rolling-hash polynomial and table are not pinned (specification 09 §3.1)."));
        }

        if (SegmentSize.Bytes == 0)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "segment_size_out_of_range",
                "The segment size is unset; construct it via SegmentSize.Create (specification 09 §2.2)."));
        }

        if (!Enum.IsDefined(DedupTrustDomain))
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "dedup_trust_domain_unknown",
                $"Trust domain {(byte)DedupTrustDomain} is not defined (specification 09 §5)."));
        }

        var compression = Compression.Validate();
        var blobProfile = BlobWriteProfile.Validate();

        if (!compression.IsValid || !blobProfile.IsValid)
        {
            defects ??= [];
            defects.AddRange(compression.Defects);
            defects.AddRange(blobProfile.Defects);
        }

        return defects is null ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(defects);
    }

    /// <summary>
    /// Validates the policy against a provider's capabilities — the check that
    /// must happen at configuration time, not at write time (FR-ARCH-007).
    /// </summary>
    public ConfigurationValidationResult ValidateAgainstStore(long maximumObjectSizeBytes)
    {
        var own = Validate();
        var store = BlobWriteProfile.ValidateAgainstStore(maximumObjectSizeBytes);

        if (own.IsValid && store.IsValid)
        {
            return ConfigurationValidationResult.Valid;
        }

        return new ConfigurationValidationResult([.. own.Defects, .. store.Defects]);
    }
}
