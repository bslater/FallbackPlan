namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// How far segment reuse may reach (specification 09 §5), recorded in the
/// policy manifest (specification 06 §7).
/// </summary>
public enum DedupTrustDomain : byte
{
    /// <summary>Reuse only segments this device wrote.</summary>
    Device = 1,

    /// <summary>
    /// Reuse any writer's segments after fetching, decrypting, and confirming
    /// the content identifier — the default.
    /// </summary>
    Repository = 2,

    /// <summary>Reuse any writer's segments without verification.</summary>
    RepositoryUnverified = 3,
}
