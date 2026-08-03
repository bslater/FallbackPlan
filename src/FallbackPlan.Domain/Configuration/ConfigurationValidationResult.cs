namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// The outcome of validating a configuration before use (FR-ARCH-007;
/// NFR-OPS-003): either valid, or a complete list of named defects — never a
/// bare boolean, and never only the first problem found.
/// </summary>
public sealed class ConfigurationValidationResult
{
    /// <summary>The valid result, shared.</summary>
    public static readonly ConfigurationValidationResult Valid = new([]);

    /// <summary>Creates a result carrying <paramref name="defects"/>.</summary>
    public ConfigurationValidationResult(IReadOnlyList<ConfigurationDefect> defects)
    {
        ArgumentNullException.ThrowIfNull(defects);
        Defects = defects;
    }

    /// <summary>Whether the configuration may be used.</summary>
    public bool IsValid => Defects.Count == 0;

    /// <summary>Every defect found, in evaluation order.</summary>
    public IReadOnlyList<ConfigurationDefect> Defects { get; }

    /// <summary>Returns whether a defect with <paramref name="name"/> is present.</summary>
    public bool Has(string name) => Defects.Any(defect => defect.Name == name);
}
