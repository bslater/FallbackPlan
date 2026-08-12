using Bodu;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Application;

/// <summary>
/// One destination's observed facts, as the derivation consumes them
/// (ADR-0027 amendment): plain values gathered by the host — the sync ledger
/// supplies the state, the platform supplies the failure-domain comparison,
/// the configuration supplies the kind.
/// </summary>
public sealed record DestinationStatusInput
{
    /// <summary>The destination's declared name.</summary>
    public required string Name { get; init; }

    /// <summary>The destination's declared kind.</summary>
    public required DestinationKind Kind { get; init; }

    /// <summary>Where the (set, destination) pair stands, per the sync ledger.</summary>
    public required DestinationSyncState Sync { get; init; }

    /// <summary>
    /// Whether the destination demonstrably sits in the source's own failure
    /// domain (same device/volume by identity comparison). True caps what it
    /// can earn at <see cref="ProtectionState.Captured"/> (PT-8) — and the
    /// staging archive never appears here at all: it is a cache, not a
    /// destination (ADR-0018 Amendment 1).
    /// </summary>
    public required bool SameFailureDomain { get; init; }

    /// <summary>When this pair last synced, Unix milliseconds; null when never.</summary>
    public ulong? LastSuccessAt { get; init; }

    /// <summary>What the last failure said, for the warning to repeat verbatim.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The facts the derivation consumes — plain values, so the derivation is
/// a pure function and this layer needs no engine, provider, or clock.
/// The host gathers them: the catalogue supplies the snapshot facts, the
/// sync ledger supplies each destination's state, and the platform supplies
/// the failure-domain comparisons.
/// </summary>
public sealed record StatusInputs
{
    /// <summary>The set's latest committed snapshot capture time; null when none exists.</summary>
    public required ulong? LatestSnapshotAt { get; init; }

    /// <summary>The latest snapshot's capture status (1 complete, 2 partial); null when none.</summary>
    public byte? LatestCaptureStatus { get; init; }

    /// <summary>The set's destinations, in declaration order (FR-DEST-004: never summarised into one flag).</summary>
    public required IReadOnlyList<DestinationStatusInput> Destinations { get; init; }

    /// <summary>Open damage findings against the set's staging archive.</summary>
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
/// <c>Protected</c> without an in-sync destination outside the source's
/// failure domain (PT-8, ADR-0034).
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

        if (inputs.DamageFindings > 0)
        {
            warnings.Add($"{inputs.DamageFindings} damage finding(s) are open — run `check`.");
            return new BackupSetStatus(ProtectionState.Degraded, inputs.LastVerification, warnings);
        }

        // The matrix, one row per destination — the truth every roll-up is
        // computed from, never invented beside (ADR-0028 §8).
        var protectedByAny = false;
        var capturedOnlyByAny = false;
        var supportedButNotInSync = false;

        foreach (var destination in inputs.Destinations)
        {
            switch (destination.Sync)
            {
                case DestinationSyncState.InSync when !destination.SameFailureDomain:
                    protectedByAny = true;
                    break;

                case DestinationSyncState.InSync:
                    capturedOnlyByAny = true;
                    warnings.Add(
                        $"'{destination.Name}' shares the source's failure domain — a safeguard against mistakes, none against losing the disk.");
                    break;

                case DestinationSyncState.NotSupported:
                    // A stated incapacity, never a failure (FR-DEST-005) —
                    // but no protection comes from it either.
                    warnings.Add($"'{destination.Name}' is not served yet: {destination.Detail ?? "kind not supported"}.");
                    break;

                default:
                    supportedButNotInSync = true;
                    warnings.Add(
                        $"'{destination.Name}' is {Describe(destination.Sync)}{(destination.Detail is null ? "" : $": {destination.Detail}")}.");
                    break;
            }
        }

        if (protectedByAny)
        {
            // Verified only ever appears WITH coverage and age (10 §1.2).
            return inputs.LastVerification is not null
                ? new BackupSetStatus(ProtectionState.Verified, inputs.LastVerification, warnings)
                : new BackupSetStatus(ProtectionState.Protected, null, warnings);
        }

        if (supportedButNotInSync)
        {
            // A destination that should hold the data does not — recoverable
            // today, and the reason each is not is already in the warnings.
            return new BackupSetStatus(ProtectionState.Degraded, inputs.LastVerification, warnings);
        }

        // What remains: in sync only within the source's failure domain, or
        // nothing but stated incapacities. Captured is the honest word —
        // commit to staging succeeded, and nothing off-domain holds a copy.
        if (!capturedOnlyByAny)
        {
            warnings.Add("No destination holds this set's data yet.");
        }

        return new BackupSetStatus(ProtectionState.Captured, inputs.LastVerification, warnings);
    }

    private static string Describe(DestinationSyncState state) => state switch
    {
        DestinationSyncState.Behind => "behind",
        DestinationSyncState.Unavailable => "unreachable",
        DestinationSyncState.Failed => "failing",
        _ => state.ToString().ToLowerInvariant(),
    };
}
