using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The source store does not promise that a listing reflects what it
    /// holds, so the keep-set cannot be trusted to name every snapshot there
    /// is ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md)).
    /// </summary>
    LaggingListing = 2,
}

/// <summary>
/// What a destination's convergence filter came out as: a filter, or a stated
/// reason there is none.
/// </summary>
/// <param name="Keeps">The filter, or null when <paramref name="Refusal"/> is set.</param>
/// <param name="Fingerprint">
/// A stable rendering of the keep-set the filter answers for, or null when
/// there is no filter. A keep-set moves with the clock rather than with
/// publication, so this is what tells a pass that a destination is owed a
/// deletion nothing published would ever reveal (ADR-0056).
/// </param>
/// <param name="Refusal">Why there is no filter, or null when there is one.</param>
public sealed record ConvergencePlan(
    Func<string, bool>? Keeps, ConvergenceRefusal? Refusal, string? Fingerprint = null)
{
    /// <summary>A filter was computed, with a stable rendering of the keep-set it answers for.</summary>
    /// <param name="keeps">The filter.</param>
    /// <param name="fingerprint">The keep-set's rendering (ADR-0056).</param>
    public static ConvergencePlan Filter(Func<string, bool> keeps, string fingerprint) =>
        new(keeps, null, fingerprint);

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

        // The keep-set is built from what a listing of this store could
        // enumerate, and it is executed as deletions at a destination that
        // may hold the only other copy. A snapshot the source's listing has
        // not caught up to would be trimmed away there on the strength of a
        // view that was incomplete — the same absence the collector refuses
        // to act on, with a worse blast radius (architecture 05 §1).
        if (store.Capabilities.ListingConsistency != ListingConsistency.Strong)
        {
            return ConvergencePlan.Refused(ConvergenceRefusal.LaggingListing);
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

        return ConvergencePlan.Filter(
            key =>
                key.StartsWith("blobs/", StringComparison.Ordinal) ? keptBlobKeys.Contains(key)
                : key.StartsWith("snapshots/", StringComparison.Ordinal) ? keptSnapshotKeys.Contains(key)
                : true,
            Fingerprint(keptBlobKeys, keptSnapshotKeys));
    }

    /// <summary>
    /// A stable rendering of a keep-set, for telling "nothing has changed"
    /// from "nothing has been published" (ADR-0056).
    /// </summary>
    /// <remarks>
    /// Order-independent, because a keep-set is a set and two listings of the
    /// same archive need not yield it in the same order. Sixteen bytes of
    /// SHA-256 per key, combined by exclusive-or and rendered with the count:
    /// the count is what stops the empty set and a pair of identical keys
    /// rendering alike. This is a change detector and not a security control —
    /// the keys it digests are the product's own, and a collision costs a
    /// skipped convergence until the reading-through comes due, never a
    /// deletion that should not have happened.
    /// </remarks>
    /// <param name="blobs">The kept blob keys.</param>
    /// <param name="snapshots">The kept snapshot keys.</param>
    private static string Fingerprint(HashSet<string> blobs, HashSet<string> snapshots)
    {
        Span<byte> combined = stackalloc byte[16];
        Span<byte> digest = stackalloc byte[32];
        var count = 0L;

        foreach (var key in blobs.Concat(snapshots))
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(key), digest);
            for (var index = 0; index < combined.Length; index++)
            {
                combined[index] ^= digest[index];
            }

            count++;
        }

        return $"{count:x}-{Convert.ToHexStringLower(combined)}";
    }

    /// <summary>The rendering of a spare set that holds nothing back.</summary>
    public const string SparePlanNothingFingerprint = "none";

    /// <summary>Whether a policy has any rule in force — an empty one converges nothing away.</summary>
    /// <param name="policy">The effective policy, or null.</param>
    public static bool HasRules(RetentionConfiguration? policy) =>
        policy is not null
        && (policy.KeepDaily is not null || policy.KeepWeekly is not null
            || policy.KeepMonthly is not null || policy.MinGenerations is not null);

    /// <summary>
    /// Each destination's keep-set under its effective policy (override, else
    /// the set's), over one shared list of facts — the per-destination
    /// keep-awareness both the replication gate (FR-GC-010) and the converge
    /// spare rest on, computed one way so they cannot disagree. Null marks a
    /// destination with no rules in force: it keeps everything.
    /// </summary>
    /// <param name="facts">The surveyed snapshots, as the planner sees them.</param>
    /// <param name="destinations">The set's declared destination references, overrides included.</param>
    /// <param name="setPolicy">The set's policy — the fallback for a reference without an override.</param>
    /// <param name="now">The clock the policy windows evaluate against.</param>
    /// <returns>Kept snapshot ids per destination name; null value means keeps-all.</returns>
    public static Dictionary<string, HashSet<string>?> KeepSetsByDestination(
        IReadOnlyList<SnapshotFact> facts,
        IReadOnlyList<SetDestinationReference> destinations,
        RetentionConfiguration? setPolicy,
        DateTimeOffset now)
    {
        ThrowHelper.ThrowIfNull(facts);
        ThrowHelper.ThrowIfNull(destinations);

        // Indexer assignment, not ToDictionary: a duplicated reference —
        // impossible through validated configuration, but callers are
        // reachable directly — must not escape the command surface as a raw
        // ArgumentException (NFR-PORT-004).
        var keptByDestination = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var reference in destinations)
        {
            var effective = reference.Retention ?? setPolicy;
            keptByDestination[reference.Ref] = !HasRules(effective)
                ? null
                : RetentionPlanner.Select(facts, effective!, now)
                    .Keep.Select(keep => keep.Snapshot.SnapshotId)
                    .ToHashSet(StringComparer.Ordinal);
        }

        return keptByDestination;
    }

    /// <summary>
    /// The converge spare (FR-GC-009's direct-ship shape): the closure of
    /// every snapshot some declared destination's own keep-set wants but the
    /// sync ledger cannot prove it received. While that is true of any
    /// sibling, no destination's converge may drop those objects — under
    /// direct-ship (ADR-0046) the replicas are the only holders, so a
    /// policy-narrow sibling trimming them would delete the last copy of
    /// history another destination is entitled to. The spare releases by
    /// itself: once the laggard's synced sequence covers a snapshot it is no
    /// longer owed, and the next pass converges everyone back to exactly
    /// their keep-sets. Disk is the cheaper failure, as the gate says.
    /// </summary>
    /// <param name="store">The archive's store — under direct-ship, the ship sink's union view.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="destinations">The set's declared destination references, overrides included.</param>
    /// <param name="setPolicy">The set's policy — the fallback for a reference without an override.</param>
    /// <param name="recordFor">The sync-ledger row for a destination, or null when never attempted.</param>
    /// <param name="nowUnixMilliseconds">The clock the policy windows evaluate against.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>
    /// The spare filter, or null when every destination provably holds every
    /// snapshot its policy keeps — the common case, costing nothing. A survey
    /// or walk that cannot be trusted answers spare-everything, because a
    /// drop decision built on a damaged graph would delete objects it merely
    /// could not see.
    /// </returns>
    public static async ValueTask<SparePlan> ComputeSparesAsync(
        IObjectStore store,
        OpenedRepository repository,
        IReadOnlyList<SetDestinationReference> destinations,
        RetentionConfiguration? setPolicy,
        Func<string, DestinationSyncRecord?> recordFor,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(destinations);
        ThrowHelper.ThrowIfNull(recordFor);

        var survey = await StagingMark.SurveyAsync(store, repository, cancellationToken).ConfigureAwait(false);
        if (survey.Undecodable.Count > 0)
        {
            return SparePlan.Everything;
        }

        var now = DateTimeOffset.FromUnixTimeMilliseconds((long)nowUnixMilliseconds);
        var facts = survey.Snapshots.Select(snapshot => snapshot.Fact).ToList();
        var keptByDestination = KeepSetsByDestination(facts, destinations, setPolicy, now);

        // The gate's own comparison — publication sequence against synced
        // sequence, proof over record of sending (FR-GC-009) — applied to
        // every snapshot rather than an expire-set: what it holds is what is
        // owed. A destination that has never synced owes everything, which is
        // exactly the conservatism a fresh ledger deserves.
        var owed = ReplicationGate.Apply(
            facts,
            [.. destinations.Select(reference => reference.Ref)],
            recordFor,
            keptBy: (name, snapshot) =>
                keptByDestination.GetValueOrDefault(name) is not { } kept || kept.Contains(snapshot.SnapshotId),
            deferralDays: null,
            nowUnixMilliseconds).Held;

        if (owed.Count == 0)
        {
            return SparePlan.Nothing;
        }

        var owedIds = owed.Select(held => held.Snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
        var owedSnapshots = survey.Snapshots
            .Where(snapshot => owedIds.Contains(snapshot.Fact.SnapshotId))
            .ToList();

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, owedSnapshots, cancellationToken)
            .ConfigureAwait(false);
        if (unwalkable.Count > 0)
        {
            return SparePlan.Everything;
        }

        var sparedSnapshotKeys = owedSnapshots.Select(snapshot => snapshot.StoreKey.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sparedBlobKeys = reader.Blobs
            .Where(blob => blob.Records.Any(record => reachable.Contains(record.ObjectId)))
            .Select(blob => blob.StoreKey.Value)
            .Concat(reader.SkippedBlobs.Select(skipped => skipped.Key.Value))
            .ToHashSet(StringComparer.Ordinal);

        return new SparePlan(
            key =>
                key.StartsWith("blobs/", StringComparison.Ordinal) ? sparedBlobKeys.Contains(key)
                : key.StartsWith("snapshots/", StringComparison.Ordinal) && sparedSnapshotKeys.Contains(key),
            Fingerprint(sparedBlobKeys, sparedSnapshotKeys));
    }
}

/// <summary>
/// What this destination must hold back for a sibling, and a stable rendering
/// of it (FR-GC-009's direct-ship shape, ADR-0056).
/// </summary>
/// <remarks>
/// The rendering exists because a spare set moves for a reason no publication
/// sequence records: a sibling catching up. Without it, a pass that skipped on
/// an unmoved watermark would go on holding copies whose only reason to exist
/// had already been delivered, and the destination would sit above its own
/// keep-set until something else happened to it.
/// </remarks>
/// <param name="Spares">The filter, or null when nothing is held back.</param>
/// <param name="Fingerprint">The spare set's rendering.</param>
public sealed record SparePlan(Func<string, bool>? Spares, string Fingerprint)
{
    /// <summary>Nothing is owed to anyone: this destination holds back nothing.</summary>
    public static SparePlan Nothing { get; } = new(null, DestinationConvergence.SparePlanNothingFingerprint);

    /// <summary>
    /// The closure would not walk, so everything is held back — a keep
    /// decision cannot be made about records nobody can read.
    /// </summary>
    public static SparePlan Everything { get; } = new(_ => true, "all");
}
