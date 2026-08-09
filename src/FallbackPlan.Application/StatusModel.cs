using Bodu;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Application;

/// <summary>
/// The facts the derivation consumes — plain values, so the derivation is
/// a pure function and this layer needs no engine, provider, or clock.
/// The host gathers them: the catalogue supplies the snapshot facts, the
/// store probe supplies reachability, and the platform supplies the
/// failure-domain comparison.
/// </summary>
public sealed record StatusInputs
{
    /// <summary>The set's latest committed snapshot capture time; null when none exists.</summary>
    public required ulong? LatestSnapshotAt { get; init; }

    /// <summary>The latest snapshot's capture status (1 complete, 2 partial); null when none.</summary>
    public byte? LatestCaptureStatus { get; init; }

    /// <summary>Whether the destination answered a probe.</summary>
    public required bool DestinationReachable { get; init; }

    /// <summary>
    /// Whether the destination demonstrably sits in the source's own
    /// failure domain (same device/volume). True caps the state at
    /// <see cref="ProtectionState.Captured"/> — the most common consumer
    /// configuration, and never reported as protected (PT-8).
    /// </summary>
    public required bool SameFailureDomain { get; init; }

    /// <summary>Open damage findings against the repository.</summary>
    public required int DamageFindings { get; init; }

    /// <summary>Whether a damage report names required objects with no readable copy.</summary>
    public required bool RequiredObjectsMissing { get; init; }

    /// <summary>The last recorded verification run, when any.</summary>
    public VerificationDetail? LastVerification { get; init; }
}

/// <summary>
/// Derives the user-level status (10 §1). The never-merge rules hold by
/// construction: <c>Degraded</c> and <c>Unrecoverable</c> are distinct
/// outcomes with distinct warnings, and <c>Captured</c> can never become
/// <c>Protected</c> without an explicit different-failure-domain fact.
/// </summary>
/// <remarks>
/// This is the single place the vocabulary is decided. A client receives the
/// result over the command surface and never re-derives it (10 §3.1), which is
/// what keeps the never-merge rules enforceable at one site rather than at
/// every front end.
/// </remarks>
public static class StatusDeriver
{
    /// <summary>Derives one set's status from observed facts.</summary>
    /// <param name="inputs">The gathered facts.</param>
    /// <returns>The derived status with its warnings.</returns>
    public static BackupSetStatus Derive(StatusInputs inputs)
    {
        ThrowHelper.ThrowIfNull(inputs);

        var warnings = new List<string>();

        if (inputs.LatestSnapshotAt is null)
        {
            return new BackupSetStatus(ProtectionState.NeverBackedUp, null, ["No snapshot has ever committed for this set."]);
        }

        // Unrecoverable means data is already gone — it outranks
        // everything and is never softened into "a problem" (10 §1.1).
        if (inputs.RequiredObjectsMissing)
        {
            return new BackupSetStatus(
                ProtectionState.Unrecoverable,
                inputs.LastVerification,
                ["Required objects are missing or damaged with no replica able to heal them."]);
        }

        if (inputs.LatestCaptureStatus == 2)
        {
            warnings.Add("The latest snapshot is PARTIAL — its error manifest names what was not captured.");
        }

        if (!inputs.DestinationReachable)
        {
            warnings.Add("The destination is unreachable.");
            return new BackupSetStatus(ProtectionState.Degraded, inputs.LastVerification, warnings);
        }

        if (inputs.DamageFindings > 0)
        {
            warnings.Add($"{inputs.DamageFindings} damage finding(s) are open — run `check`.");
            return new BackupSetStatus(ProtectionState.Degraded, inputs.LastVerification, warnings);
        }

        if (inputs.SameFailureDomain)
        {
            warnings.Add(
                "The repository shares the source's failure domain — a safeguard against mistakes, none against losing the disk.");
            return new BackupSetStatus(ProtectionState.Captured, inputs.LastVerification, warnings);
        }

        // Verified only ever appears WITH coverage and age (10 §1.2).
        return inputs.LastVerification is not null
            ? new BackupSetStatus(ProtectionState.Verified, inputs.LastVerification, warnings)
            : new BackupSetStatus(ProtectionState.Protected, null, warnings);
    }
}
