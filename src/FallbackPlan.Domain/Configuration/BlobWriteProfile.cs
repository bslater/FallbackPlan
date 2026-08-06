namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// Blob sealing targets (specification 05 §5): when a blob is considered full
/// enough to seal, and the hard sizes it must never exceed. Validated against
/// the format's limits at configuration time, and against a provider's
/// capabilities before any write is attempted (FR-ARCH-007; NFR-OPS-003).
/// </summary>
public sealed record BlobWriteProfile
{
    /// <summary>The specification defaults for local and peer stores: seal at 64 MiB, never exceed 256 MiB.</summary>
    public static readonly BlobWriteProfile LocalDefault = new()
    {
        TargetSizeBytes = 64L * 1024 * 1024,
        MaximumSizeBytes = 256L * 1024 * 1024,
        MaximumRecordCount = FormatLimits.MaxRecordsPerBlob,
        OpenBlobMaximumAge = TimeSpan.FromMinutes(15),
    };

    /// <summary>The specification defaults for object stores: seal at 128 MiB, never exceed 512 MiB.</summary>
    public static readonly BlobWriteProfile ObjectStoreDefault = new()
    {
        TargetSizeBytes = 128L * 1024 * 1024,
        MaximumSizeBytes = 512L * 1024 * 1024,
        MaximumRecordCount = FormatLimits.MaxRecordsPerBlob,
        OpenBlobMaximumAge = TimeSpan.FromMinutes(15),
    };

    /// <summary>The size at which the writer seals the open blob.</summary>
    public required long TargetSizeBytes { get; init; }

    /// <summary>The size the next record must not push the blob past.</summary>
    public required long MaximumSizeBytes { get; init; }

    /// <summary>The record count that forces sealing.</summary>
    public required int MaximumRecordCount { get; init; }

    /// <summary>The age at which an open blob is sealed regardless of fill.</summary>
    public required TimeSpan OpenBlobMaximumAge { get; init; }

    /// <summary>Validates the profile against the format's own limits.</summary>
    public ConfigurationValidationResult Validate()
    {
        List<ConfigurationDefect>? defects = null;

        if (TargetSizeBytes <= 0 || TargetSizeBytes > MaximumSizeBytes)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "blob_target_exceeds_maximum",
                $"Target size {TargetSizeBytes} must be positive and no greater than the maximum {MaximumSizeBytes}."));
        }

        if (MaximumSizeBytes > FormatLimits.MaxBlobSize)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "blob_maximum_exceeds_format_limit",
                $"Maximum size {MaximumSizeBytes} exceeds the format's blob limit of {FormatLimits.MaxBlobSize} (specification 00 §8)."));
        }

        if (MaximumRecordCount is <= 0 or > FormatLimits.MaxRecordsPerBlob)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "blob_record_count_exceeds_format_limit",
                $"Record count {MaximumRecordCount} is outside 1–{FormatLimits.MaxRecordsPerBlob} (specification 00 §8)."));
        }

        if (OpenBlobMaximumAge <= TimeSpan.Zero)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "blob_open_age_not_positive",
                "The open-blob maximum age must be positive."));
        }

        return defects is null ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(defects);
    }

    /// <summary>
    /// Validates the profile against a provider's maximum object size —
    /// A5's acceptance criterion: a profile exceeding a provider limit is
    /// rejected at configuration time with a named reason, not discovered at
    /// write time (FR-ARCH-007; NFR-OPS-003).
    /// </summary>
    public ConfigurationValidationResult ValidateAgainstStore(long maximumObjectSizeBytes)
    {
        if (MaximumSizeBytes > maximumObjectSizeBytes)
        {
            return new ConfigurationValidationResult(
            [
                new ConfigurationDefect(
                    "blob_maximum_exceeds_provider_object_size",
                    $"The blob maximum of {MaximumSizeBytes} bytes exceeds the provider's maximum object size of {maximumObjectSizeBytes} bytes."),
            ]);
        }

        return ConfigurationValidationResult.Valid;
    }
}
