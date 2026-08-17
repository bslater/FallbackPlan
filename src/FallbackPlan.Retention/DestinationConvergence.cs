using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>
/// Computes what a destination under a retention policy is entitled to hold
/// (FR-GC-010, ADR-0009 Amendment 4): the hub selects that destination's
/// keep-set, marks its closure against the staging archive — only the hub
/// can read manifests — and answers per store key. The destination never
/// decides; it receives the difference.
/// </summary>
/// <summary>Why a destination is not converging under its policy.</summary>
/// <remarks>
/// The conservative whole copy is the right answer to all of these, and it is
/// the reason they were once indistinguishable: the behaviour is identical, so
/// the distinction looked academic. It is not. "This destination has no
/// retention rules" is a configuration choice; "the staging graph would not
/// walk" is damage that will keep the destination holding history it was told
/// to drop until someone repairs it. Reporting them the same way means the
/// second never gets repaired.
/// </remarks>
public enum ConvergenceRefusal
{
    /// <summary>A survey found snapshots it could not decode.</summary>
    UndecodableSnapshots = 0,

    /// <summary>The keep-set's closure would not walk — a reference led somewhere unreadable.</summary>
    UnwalkableClosure = 1,
}

/// <summary>
/// What a destination's convergence filter came out as: a filter, or a stated
/// reason there is none.
/// </summary>
/// <param name="Keeps">The filter, or null when <paramref name="Refusal"/> is set.</param>
/// <param name="Refusal">Why there is no filter, or null when there is one.</param>
public sealed record ConvergencePlan(Func<string, bool>? Keeps, ConvergenceRefusal? Refusal)
{
    /// <summary>A filter was computed.</summary>
    public static ConvergencePlan Filter(Func<string, bool> keeps) => new(keeps, null);

    /// <summary>No filter: the destination gets the conservative whole copy, for this reason.</summary>
    public static ConvergencePlan Refused(ConvergenceRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Computes what a destination under a retention policy is entitled to hold
/// (FR-GC-010, ADR-0009 Amendment 4): the hub selects that destination's
/// keep-set, marks its closure against the staging archive — only the hub
/// can read manifests — and answers per store key. The destination never
/// decides; it receives the difference.
/// </summary>
public static class DestinationConvergence
{
    /// <summary>
    /// Builds the keep filter for one destination's effective policy, or says
    /// why it could not — the caller then converges nothing away and copies
    /// whole, because a keep decision built on a damaged graph would drop
    /// objects it merely could not see.
    /// </summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened staging archive.</param>
    /// <param name="policy">The destination's effective policy (override, else the set's).</param>
    /// <param name="now">The clock the policy windows evaluate against.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The filter, or the reason the conservative whole copy is being demanded.</returns>
    public static async ValueTask<ConvergencePlan> ComputeKeepsAsync(
        IObjectStore store,
        OpenedRepository repository,
        RetentionConfiguration policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(policy);

        var survey = await StagingMark.SurveyAsync(store, repository, cancellationToken).ConfigureAwait(false);
        if (survey.Undecodable.Count > 0)
        {
            return ConvergencePlan.Refused(ConvergenceRefusal.UndecodableSnapshots);
        }

        var selection = RetentionPlanner.Select(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)], policy, now);
        var keepIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
        var keptSnapshots = survey.Snapshots
            .Where(snapshot => keepIds.Contains(snapshot.Fact.SnapshotId))
            .ToList();

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, keptSnapshots, cancellationToken)
            .ConfigureAwait(false);
        if (unwalkable.Count > 0)
        {
            return ConvergencePlan.Refused(ConvergenceRefusal.UnwalkableClosure);
        }

        var keptSnapshotKeys = keptSnapshots.Select(snapshot => snapshot.StoreKey.Value)
            .ToHashSet(StringComparer.Ordinal);

        // A blob travels if anything the destination keeps lives in it; a
        // blob the staging reader had to skip travels too, because a keep
        // decision cannot be made about records nobody can read.
        var keptBlobKeys = reader.Blobs
            .Where(blob => blob.Records.Any(record => reachable.Contains(record.ObjectId)))
            .Select(blob => blob.StoreKey.Value)
            .Concat(reader.SkippedBlobs.Select(skipped => skipped.Key.Value))
            .ToHashSet(StringComparer.Ordinal);

        return ConvergencePlan.Filter(key =>
            key.StartsWith("blobs/", StringComparison.Ordinal) ? keptBlobKeys.Contains(key)
            : key.StartsWith("snapshots/", StringComparison.Ordinal) ? keptSnapshotKeys.Contains(key)
            : true);
    }

    /// <summary>Whether a policy has any rule in force — an empty one converges nothing away.</summary>
    /// <param name="policy">The effective policy, or null.</param>
    public static bool HasRules(RetentionConfiguration? policy) =>
        policy is not null
        && (policy.KeepDaily is not null || policy.KeepWeekly is not null
            || policy.KeepMonthly is not null || policy.MinGenerations is not null);
}
