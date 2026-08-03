namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// Which side of the KDF-parameter contract applies (specification 03 §2): a
/// writer creating a repository must refuse parameters below the mandated
/// minimums, while a reader opening an existing repository must accept lower
/// values — they are facts about stored bytes — and warn.
/// </summary>
public enum KdfValidationMode
{
    /// <summary>Creating a new repository: minimums are enforced, refusal not warning.</summary>
    CreateRepository,

    /// <summary>Opening an existing repository: lower values accepted with a warning.</summary>
    OpenRepository,
}
