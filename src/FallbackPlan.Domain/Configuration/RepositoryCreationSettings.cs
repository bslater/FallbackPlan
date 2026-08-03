using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// Everything repository creation needs to be told, validated before any byte
/// is written (specification 01 §3, 03 §2; FR-ARCH-007). Creation-mode KDF
/// minimums are enforced here as refusals, not warnings.
/// </summary>
public sealed record RepositoryCreationSettings
{
    /// <summary>The creation defaults: mandated-minimum Argon2id, AES-256-GCM.</summary>
    public static readonly RepositoryCreationSettings Default = new()
    {
        KdfParameters = Argon2Parameters.CreationMinimums,
        EncryptionProfile = EncryptionProfile.Aes256GcmV1,
        CreatedBy = "fallbackplan",
    };

    /// <summary>Argon2id cost parameters for the descriptor.</summary>
    public required Argon2Parameters KdfParameters { get; init; }

    /// <summary>The AEAD suite records will use, from the approved set only.</summary>
    public required EncryptionProfile EncryptionProfile { get; init; }

    /// <summary>The client identification written to the descriptor's created_by field.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Validates the settings in creation mode, aggregating every named defect.</summary>
    public ConfigurationValidationResult Validate()
    {
        var kdf = KdfParameters.ValidateCreationMinimums();

        List<ConfigurationDefect>? defects = kdf.IsValid ? null : [.. kdf.Defects];

        if (string.IsNullOrWhiteSpace(CreatedBy))
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "created_by_empty",
                "The created_by identification must not be empty (specification 01 §3.2)."));
        }

        return defects is null ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(defects);
    }
}
