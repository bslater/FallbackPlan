using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// Compression policy (specification 10): the profile, the zstd level, and the
/// storage threshold that decides when a compressed form is worth storing.
/// </summary>
public sealed record CompressionSettings
{
    /// <summary>The lowest permitted zstd level (specification 10 §4).</summary>
    public const int MinimumZstdLevel = 1;

    /// <summary>The highest permitted zstd level; levels above 19 are not permitted (specification 10 §4).</summary>
    public const int MaximumZstdLevel = 19;

    /// <summary>The specification defaults: zstd level 3, threshold 50 permille.</summary>
    public static readonly CompressionSettings Default = new()
    {
        Profile = CompressionProfile.ZstdV1,
        ZstdLevel = 3,
        ThresholdPermille = 50,
    };

    /// <summary>The compression profile.</summary>
    public required CompressionProfile Profile { get; init; }

    /// <summary>The zstd compression level, 1–19 (ignored when the profile is <c>none</c>).</summary>
    public required int ZstdLevel { get; init; }

    /// <summary>
    /// The storage threshold in permille (specification 10 §3): the compressed
    /// form is stored only when it saves at least this fraction.
    /// </summary>
    public required ushort ThresholdPermille { get; init; }

    /// <summary>Validates the settings, reporting every named defect.</summary>
    public ConfigurationValidationResult Validate()
    {
        List<ConfigurationDefect>? defects = null;

        if (Profile == CompressionProfile.ZstdV1 && ZstdLevel is < MinimumZstdLevel or > MaximumZstdLevel)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "compression_level_out_of_range",
                $"zstd level {ZstdLevel} is outside {MinimumZstdLevel}–{MaximumZstdLevel} (specification 10 §4)."));
        }

        if (ThresholdPermille > 1000)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "compression_threshold_out_of_range",
                $"Threshold {ThresholdPermille} permille exceeds 1000 (specification 10 §3)."));
        }

        return defects is null ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(defects);
    }
}
