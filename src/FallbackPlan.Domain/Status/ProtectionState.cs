namespace FallbackPlan.Domain.Status;

/// <summary>
/// The normative status vocabulary (architecture 10 §1.1; NFR-OPS-002).
/// A closed set, because collapsing any two of these is how a user comes
/// to believe they are protected when they are not.
/// </summary>
/// <remarks>
/// It lives in Domain so the command contract can carry it to a client without
/// referencing the application layer that derives it (11 §2). Derivation stays
/// in one place — a front end that computed its own would be a second
/// implementation of the never-merge rules, free to drift from the first.
/// </remarks>
public enum ProtectionState
{
    /// <summary>No committed snapshot exists for the set.</summary>
    NeverBackedUp = 0,

    /// <summary>Committed, but only within the source's own failure domain — real, and no defence against losing the machine.</summary>
    Captured = 1,

    /// <summary>Durable at a replica separate from the drive the source lives on (ADR-0051; PT-8's same-disk confidence stays impossible).</summary>
    Protected = 2,

    // 3 and 5 are retired, and their numbers stay reserved: the contract
    // serializer carries this enum numerically, so a reassigned value would
    // change meaning under a deployed client. Replicated (3) said nothing
    // the Captured/Protected failure-domain distinction does not say more
    // precisely, and PolicyCompliant (5) presupposed a durability policy
    // that does not exist; neither was ever emitted by the deriver.

    /// <summary>Independently confirmed at that destination — always with coverage and age, never a bare tick.</summary>
    Verified = 4,

    /// <summary>Recoverable, but below policy — act soon.</summary>
    Degraded = 6,

    /// <summary>Required objects are missing or damaged with no replica able to heal them — data is already gone.</summary>
    Unrecoverable = 7,
}

/// <summary>A verification claim: never shown without its coverage and age (10 §1.2).</summary>
/// <param name="Coverage">The fraction of the set's objects the run covered.</param>
/// <param name="VerifiedAtUnixMilliseconds">When the run completed.</param>
public sealed record VerificationDetail(double Coverage, ulong VerifiedAtUnixMilliseconds);

/// <summary>One backup set's derived status.</summary>
/// <param name="State">The derived vocabulary term.</param>
/// <param name="Verification">The verification claim, when one exists.</param>
/// <param name="Warnings">What the user should know, in the order it matters.</param>
public sealed record BackupSetStatus(
    ProtectionState State,
    VerificationDetail? Verification,
    IReadOnlyList<string> Warnings);
