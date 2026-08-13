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
        Concurrency = DefaultConcurrency,
    };

    /// <summary>
    /// The default concurrency: deliberately below the machine's capacity.
    /// </summary>
    /// <remarks>
    /// NFR-OPS-004 requires defaults safe on a 4-core laptop and NFR-PERF-013
    /// caps measured background CPU at 30% over any 60-second window. A backup
    /// that makes the machine unpleasant to use gets switched off, and a
    /// switched-off backup protects nothing — so saturating the hardware is an
    /// opt-in for someone who has decided this machine is a backup machine.
    /// </remarks>
    public const int DefaultConcurrency = 2;

    /// <summary>The largest concurrency this policy will accept.</summary>
    /// <remarks>
    /// A ceiling rather than a preference: memory is bounded by
    /// <c>concurrency × segment size × a small constant</c> (NFR-PERF-001), and
    /// an unbounded setting would make that bound unstatable.
    /// </remarks>
    public const int MaximumConcurrency = 64;

    /// <summary>The segmentation profile.</summary>
    public required SegmentationProfile SegmentationProfile { get; init; }

    /// <summary>The fixed-v1 segment size; ignored under cdc-v1.</summary>
    public required SegmentSize SegmentSize { get; init; }

    /// <summary>The cdc-v1 parameters; required under cdc-v1, absent otherwise.</summary>
    public CdcParameters? CdcParameters { get; init; }

    /// <summary>Compression policy.</summary>
    public required CompressionSettings Compression { get; init; }

    /// <summary>The AEAD suite for records, from the approved set only.</summary>
    public required EncryptionProfile EncryptionProfile { get; init; }

    /// <summary>Blob sealing targets.</summary>
    public required BlobWriteProfile BlobWriteProfile { get; init; }

    /// <summary>The segment-reuse trust domain.</summary>
    public required DedupTrustDomain DedupTrustDomain { get; init; }

    /// <summary>
    /// The explicit acknowledgement <see cref="Configuration.DedupTrustDomain.RepositoryUnverified"/>
    /// requires (FR-DED-004): setting it records that a faulty or hostile
    /// writer can corrupt other devices' backups. The domain cannot be
    /// enabled without it — validation names the defect and the publication
    /// pipeline refuses to construct.
    /// </summary>
    public bool AcknowledgesUnverifiedDedupRisk { get; init; }

    /// <summary>
    /// How much of the pipeline may run at once (ADR-0029 §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One value, not two, because NFR-PERF-001 bounds memory by "configured
    /// concurrency" and two independent knobs would make that bound
    /// unstatable. It bounds the concurrent stage — read, hash, compress — and
    /// the upload workers together.
    /// </para>
    /// <para>
    /// <b>1 is a valid and tested setting</b>: it is the configuration in which
    /// the ordering barrier is trivially satisfied, and it is the control case
    /// for NFR-PERF-002's acceptance test that restored bytes are identical
    /// regardless of this number.
    /// </para>
    /// </remarks>
    public int Concurrency { get; init; } = DefaultConcurrency;

    /// <summary>
    /// The largest segment this policy can produce — what bounds working
    /// buffers (NFR-PERF-001): the fixed size under fixed-v1, the maximum
    /// under cdc-v1.
    /// </summary>
    public int MaximumSegmentBytes =>
        SegmentationProfile == SegmentationProfile.CdcV1
            ? CdcParameters?.MaxSize ?? 0
            : SegmentSize.Bytes;

    /// <summary>
    /// Validates the whole policy, aggregating every named defect from every
    /// section.
    /// </summary>
    public ConfigurationValidationResult Validate()
    {
        List<ConfigurationDefect>? defects = null;

        if (SegmentationProfile == SegmentationProfile.CdcV1)
        {
            if (CdcParameters is not { TargetSize: > 0 })
            {
                (defects ??= []).Add(new ConfigurationDefect(
                    "cdc_parameters_missing",
                    "cdc-v1 requires validated parameters; construct them via CdcParameters.Create (specification 09 §3.1; ADR-0023)."));
            }
        }
        else
        {
            if (SegmentSize.Bytes == 0)
            {
                (defects ??= []).Add(new ConfigurationDefect(
                    "segment_size_out_of_range",
                    "The segment size is unset; construct it via SegmentSize.Create (specification 09 §2.2)."));
            }

            if (CdcParameters is not null)
            {
                (defects ??= []).Add(new ConfigurationDefect(
                    "cdc_parameters_without_cdc_profile",
                    "cdc parameters are set but the segmentation profile is not cdc-v1 — one of the two is a mistake."));
            }
        }

        if (Concurrency is < 1 or > MaximumConcurrency)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "concurrency_out_of_range",
                $"Concurrency {Concurrency} is outside 1..{MaximumConcurrency}; memory is bounded by concurrency × "
                + "segment size (NFR-PERF-001), so an unbounded setting makes the bound unstatable."));
        }

        if (!Enum.IsDefined(DedupTrustDomain))
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "dedup_trust_domain_unknown",
                $"Trust domain {(byte)DedupTrustDomain} is not defined (specification 09 §5)."));
        }

        if (DedupTrustDomain == DedupTrustDomain.RepositoryUnverified && !AcknowledgesUnverifiedDedupRisk)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "unverified_dedup_unacknowledged",
                "The repository-unverified trust domain reuses other writers' segments without reading them, "
                + "so a faulty or hostile writer can corrupt this device's backups. It cannot be enabled "
                + "without acknowledging that risk (FR-DED-004)."));
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
