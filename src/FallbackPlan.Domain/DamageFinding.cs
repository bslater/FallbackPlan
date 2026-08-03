namespace FallbackPlan.Domain;

/// <summary>
/// The damage classes recovery reports (architecture 02 §8.3; FR-MAN-011,
/// FR-MAN-012): each fault produces its own distinctly named kind, because
/// "the repository is damaged" tells a user nothing about what to do next.
/// </summary>
public enum DamageKind
{
    /// <summary>The local catalogue is corrupt — rebuildable, never authoritative.</summary>
    CatalogueCorruption = 1,

    /// <summary>An index delta or checkpoint is missing, unreadable, or gapped past patience (07 §4).</summary>
    MissingIndexObject = 2,

    /// <summary>A referenced blob is absent from the store (01 §5).</summary>
    MissingBlob = 3,

    /// <summary>A record failed authentication or content verification (04 §7).</summary>
    CorruptRecord = 4,

    /// <summary>Stored data no live object references.</summary>
    OrphanData = 5,

    /// <summary>A signature failed — substitution or forgery, not a bad disk (06 §6.1).</summary>
    SecurityFinding = 6,
}

/// <summary>One damage finding: reported, never repaired in place (07 §10).</summary>
public sealed record DamageFinding(DamageKind Kind, string Detail);
