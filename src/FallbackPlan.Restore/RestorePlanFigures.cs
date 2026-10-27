using FallbackPlan.Domain;

namespace FallbackPlan.Restore;

/// <summary>
/// The figures a restore's plan reports beside its items (FR-RST-003): what
/// its files write, the conflicts the planner found, and what the target will
/// not get back. One reckoning for the service's plan and the command line's
/// own, so a plan reads alike whichever made it (ADR-0028 §3, ADR-0107).
/// </summary>
public static class RestorePlanFigures
{
    /// <summary>
    /// What the plan's files write: each as its manifest says, holes skipped,
    /// or its logical length where the probe could not read the manifest.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="probed">What the probe read of the plan's manifests.</param>
    public static ulong WriteBytes(RestorePlan plan, RestoreBlobSetResult probed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(probed);
        return plan.Items
            .Where(item => item.Kind == EntryKind.File)
            .Aggregate(0ul, (sum, item) => sum
                + (probed.Facts.TryGetValue(item.ObjectId, out var known) ? known.WrittenBytes : item.Length));
    }

    /// <summary>Each conflict the planner found, as its path and why.</summary>
    /// <param name="plan">The plan.</param>
    public static List<string> Conflicts(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Conflicts.Select(conflict => $"{conflict.Path} — {conflict.Reason}")];
    }

    /// <summary>
    /// What the target will not get back: the planner's degradations, then
    /// the captured metadata the probe found that this target will not write.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="probed">What the probe read of the plan's manifests.</param>
    /// <param name="target">The target the run would write to.</param>
    public static List<string> Degradations(RestorePlan plan, RestoreBlobSetResult probed, RestoreTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(probed);
        return
        [
            .. plan.Degradations.Concat(RestoreMetadata.Declare(plan, probed.Facts, target))
                .Select(degradation => degradation.Detail),
        ];
    }
}
