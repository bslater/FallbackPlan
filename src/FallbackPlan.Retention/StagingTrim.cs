using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>
/// How one destination's copy of a staging blob can be verified before the
/// staging copy is allowed to go (ADR-0034 §6). The decision of WHICH kind
/// applies belongs to the caller — the engine only knows how to use each.
/// </summary>
public abstract record TrimVerification
{
    /// <summary>A ledger-claim verification — see <see cref="LedgerClaim"/>.</summary>
    public static readonly TrimVerification Ledger = new LedgerClaim();

    /// <summary>No verification is possible — see <see cref="Unverifiable"/>.</summary>
    public static readonly TrimVerification None = new Unverifiable();

    /// <summary>The destination declines to prove anything — see <see cref="Unprovable"/>.</summary>
    public static readonly TrimVerification Declined = new Unprovable();

    /// <summary>A direct per-key probe — see <see cref="StoreProbe"/>.</summary>
    public static TrimVerification AgainstStore(IObjectStore replica) => new StoreProbe(replica);

    /// <summary>
    /// The destination's store is reachable: each blob is verified by asking
    /// that store for the key's metadata — direct evidence, blob by blob.
    /// </summary>
    /// <param name="Replica">The destination's replica store.</param>
    public sealed record StoreProbe(IObjectStore Replica) : TrimVerification;

    /// <summary>
    /// The destination is remote: a blob reachable from a snapshot the
    /// destination keeps is verified when the sync ledger shows a completed
    /// sync at or past the staging archive's current publication sequence —
    /// the same trust the replication gate already rests on (FR-GC-009). A
    /// blob no surveyed snapshot reaches can never be verified this way.
    /// </summary>
    public sealed record LedgerClaim : TrimVerification;

    /// <summary>
    /// Nothing can vouch for this destination right now — an unplugged
    /// drive, an unsupported kind. Every blob it is entitled to stays.
    /// </summary>
    public sealed record Unverifiable : TrimVerification;

    /// <summary>
    /// The destination is declared unprovable by its own configuration
    /// (<c>verification: acknowledged-none</c>). Distinct from
    /// <see cref="Unverifiable"/> because "right now" is false of it: nothing
    /// will change on its own, and the report must say so rather than imply a
    /// wait. You cannot both refuse to prove and authorise a deletion
    /// (FR-VER-006).
    /// </summary>
    public sealed record Unprovable : TrimVerification;
}

/// <summary>What one trim pass may remove, and why the rest stays.</summary>
/// <param name="Eligible">Data blobs every entitled destination verifiably holds.</param>
/// <param name="EligibleBytes">Their total size.</param>
/// <param name="HeldBack">Historic data blobs some entitled destination could not vouch for.</param>
/// <param name="Lines">The report lines, dry run and apply alike (FR-GC-005).</param>
public sealed record TrimPlan(
    IReadOnlyList<TrimCandidate> Eligible,
    long EligibleBytes,
    int HeldBack,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// The publication sequence the plan reasoned about, carried so the
    /// deletion pass can re-demand a proof covering the same ground rather
    /// than settling for any proof at all.
    /// </summary>
    public ulong PublicationSequence { get; init; }
}

/// <summary>One blob the trim may remove.</summary>
/// <param name="StoreKey">The staging store key.</param>
/// <param name="Length">Its size, for the report.</param>
/// <param name="EntitledDestinations">The destinations whose verified copies the decision rests on — re-checked at deletion time.</param>
public sealed record TrimCandidate(ObjectKey StoreKey, long Length, IReadOnlyList<string> EntitledDestinations);

/// <summary>
/// The staging trim (ADR-0034 §6): drops HISTORIC data blobs from the staging
/// archive once every destination entitled to them verifiably holds them, so
/// a hub stops paying for history twice. Staging is a cache (ADR-0011
/// Amendment); this is the cache dropping entries under the FR-GC-009 trust
/// discipline — executed as direct deletes, not tombstones, because a trimmed
/// blob is still reachable from kept snapshots and the spec-11 revalidation
/// would rightly veto it.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>blobs/data/</c> is ever eligible. All metadata — meta blobs,
/// snapshots, index, journal, keys, descriptor — stays in staging, which is
/// what keeps every existing derivation working after a trim: the closure
/// walk reads only metadata records, publication sequencing is untouched,
/// and the planner, gate and marker still see the full history.
/// </para>
/// <para>
/// The newest snapshot's closure also stays, deliberately. The dedup trust
/// gate probes staging before every reuse (a stale-catalogue guard), so
/// trimming the current generation would make the next backup re-store every
/// unchanged file into new blobs and fan them out again — and the convergence
/// rule that protects trimmed objects (unknown is kept) would then let the
/// superseded copies pile up at every destination forever. Keeping the
/// current closure cached makes trim converge instead of oscillate: what
/// leaves staging is history, and history does not come back.
/// </para>
/// </remarks>
public static class StagingTrim
{
    /// <summary>Decides what may leave staging. Deletes nothing.</summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="reader">The footer-truth reader, blobs already loaded.</param>
    /// <param name="survey">The snapshot survey of the same store.</param>
    /// <param name="setPolicy">The set's retention policy, or null.</param>
    /// <param name="destinations">The set's declared destination references.</param>
    /// <param name="verificationFor">How each destination's holdings can be verified.</param>
    /// <param name="syncRecordFor">The sync-ledger row for a destination (the ledger claim's input).</param>
    /// <param name="intents">The live-intent survey — a covered blob belongs to a publication in flight and never trims.</param>
    /// <param name="now">The clock the policy windows evaluate against.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The plan, with its report lines.</returns>
    public static async ValueTask<TrimPlan> PlanAsync(
        IObjectStore store,
        RepositoryReader reader,
        SnapshotSurvey survey,
        RetentionConfiguration? setPolicy,
        IReadOnlyList<SetDestinationReference> destinations,
        Func<string, TrimVerification> verificationFor,
        Func<string, DestinationSyncRecord?> syncRecordFor,
        IntentSurvey intents,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(survey);
        ThrowHelper.ThrowIfNull(destinations);
        ThrowHelper.ThrowIfNull(verificationFor);
        ThrowHelper.ThrowIfNull(syncRecordFor);
        ThrowHelper.ThrowIfNull(intents);

        if (survey.Undecodable.Count > 0)
        {
            return Nothing($"trim: skipped — {survey.Undecodable.Count} undecodable snapshot object(s)");
        }

        if (destinations.Count == 0)
        {
            return Nothing("trim: skipped — no destinations declared, staging is the only copy");
        }

        // The whole-history closure: the ledger claim can only vouch for a
        // blob some surveyed snapshot reaches, because only those were ever
        // part of a push. And the newest snapshot's closure within it is the
        // current generation — the dedup cache that never trims.
        var (fullClosure, fullUnwalkable) = await StagingMark.MarkAsync(reader, survey.Snapshots, cancellationToken)
            .ConfigureAwait(false);
        if (fullUnwalkable.Count > 0)
        {
            return Nothing("trim: skipped — the staging graph would not walk cleanly (full history)");
        }

        var currentBlobKeys = new HashSet<string>(StringComparer.Ordinal);
        if (survey.Snapshots.Count > 0)
        {
            var newest = survey.Snapshots.MaxBy(snapshot => snapshot.Fact.PublicationSequence)!;
            var (currentClosure, currentUnwalkable) = await StagingMark.MarkAsync(reader, [newest], cancellationToken)
                .ConfigureAwait(false);
            if (currentUnwalkable.Count > 0)
            {
                return Nothing("trim: skipped — the staging graph would not walk cleanly (newest snapshot)");
            }

            foreach (var blob in reader.Blobs)
            {
                if (blob.Records.Any(record => currentClosure.Contains(record.ObjectId)))
                {
                    currentBlobKeys.Add(blob.StoreKey.Value);
                }
            }
        }

        // Per-destination entitlement: a destination under rules is entitled
        // to its keep-set's closure; one without rules is entitled to the
        // whole archive. Null means "everything".
        var entitledBlobKeys = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var reference in destinations)
        {
            var effective = reference.Retention ?? setPolicy;
            if (!DestinationConvergence.HasRules(effective))
            {
                entitledBlobKeys[reference.Ref] = null;
                continue;
            }

            var selection = RetentionPlanner.Select(
                [.. survey.Snapshots.Select(snapshot => snapshot.Fact)], effective!, now);
            var keepIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId).ToHashSet(StringComparer.Ordinal);
            var kept = survey.Snapshots.Where(snapshot => keepIds.Contains(snapshot.Fact.SnapshotId)).ToList();

            var (closure, unwalkable) = await StagingMark.MarkAsync(reader, kept, cancellationToken)
                .ConfigureAwait(false);
            if (unwalkable.Count > 0)
            {
                return Nothing(
                    $"trim: skipped — the staging graph would not walk cleanly (destination '{reference.Ref}')");
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var blob in reader.Blobs)
            {
                if (blob.Records.Any(record => closure.Contains(record.ObjectId)))
                {
                    keys.Add(blob.StoreKey.Value);
                }
            }

            entitledBlobKeys[reference.Ref] = keys;
        }

        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            lengths[entry.Key.Value] = entry.Length;
        }

        var maxPublicationSequence = survey.Snapshots.Count == 0
            ? 0UL
            : survey.Snapshots.Max(snapshot => snapshot.Fact.PublicationSequence);
        var nowUnixMilliseconds = (ulong)now.ToUnixTimeMilliseconds();

        var eligible = new List<TrimCandidate>();
        var eligibleBytes = 0L;
        var heldBack = 0;

        // Candidates come from the loaded footers, never the raw listing: a
        // blob the reader had to skip is one nobody can reason about, and it
        // stays until verify names its damage.
        // Which destination stalled the trim, and why. Retention stalling with
        // nothing but a count is the shape an operator cannot act on, so every
        // blocking destination earns a named line (FR-GC-005).
        var blocked = new SortedDictionary<string, TrimHold>(StringComparer.Ordinal);
        foreach (var blob in reader.Blobs)
        {
            var key = blob.StoreKey.Value;
            if (!key.StartsWith("blobs/data/", StringComparison.Ordinal)
                || currentBlobKeys.Contains(key)
                || intents.IsCovered(blob.BlobId)
                // Only HISTORY trims: a blob no surveyed snapshot reaches is
                // unreferenced debris — the tombstone cycle's business, with
                // its grace and revalidation, never the trim's direct delete.
                || !blob.Records.Any(record => fullClosure.Contains(record.ObjectId)))
            {
                continue;
            }

            var entitled = destinations
                .Where(reference => entitledBlobKeys[reference.Ref] is not { } keys || keys.Contains(key))
                .ToList();
            if (entitled.Count == 0)
            {
                // Reachable, yet no destination keeps it — every policy
                // dropped its snapshots. Staging keeps it until they expire.
                continue;
            }

            var verified = true;
            foreach (var reference in entitled)
            {
                var verification = verificationFor(reference.Ref);

                // Deleting the last local copy is licensed by PROOF, never by
                // a claim (FR-VER-006). Both bases now require a verification
                // stamp covering this pass's sequence: a destination that has
                // not read bytes back to us recently does not get to authorise
                // us forgetting them.
                var proven = Proves(syncRecordFor(reference.Ref), maxPublicationSequence);

                var hold = verification switch
                {
                    TrimVerification.Unprovable => TrimHold.Declared,
                    TrimVerification.Unverifiable => TrimHold.Unreachable,
                    // Whatever the basis, an unproven destination stalls here
                    // first — naming the missing proof beats naming whichever
                    // second test would also have failed.
                    _ when !proven => TrimHold.Unproven,
                    // Two different facts, both needed: the probe says this
                    // exact key is present, the stamp says the destination
                    // genuinely holds real bytes rather than plausible-looking
                    // empty files.
                    TrimVerification.StoreProbe probe =>
                        await HoldsAsync(probe.Replica, blob.StoreKey, cancellationToken).ConfigureAwait(false)
                            ? null
                            : TrimHold.DoesNotHold,
                    // The claim also demands this pass's clock be at or past
                    // the sync it rests on: entitlement here is computed at
                    // `now`, and a pass running BEHIND the last sync could
                    // hold a wider keep-set than the one the destination
                    // actually converged to — vouching for a blob the
                    // destination was instructed to drop.
                    TrimVerification.LedgerClaim =>
                        maxPublicationSequence > 0
                        && syncRecordFor(reference.Ref) is { } record
                        && record.SyncedSequence >= maxPublicationSequence
                        && record.LastSuccessAt is { } lastSuccess
                        && nowUnixMilliseconds >= lastSuccess
                            ? null
                            : (TrimHold?)TrimHold.Unproven,
                    _ => TrimHold.Unreachable,
                };

                if (hold is { } reason)
                {
                    // First reason wins: a destination that stalls on one blob
                    // stalls on the rest for the same cause, and one line per
                    // destination is the point.
                    blocked.TryAdd(reference.Ref, reason);
                    verified = false;
                    break;
                }
            }

            if (!verified)
            {
                heldBack++;
                continue;
            }

            var length = lengths.GetValueOrDefault(key);
            eligible.Add(new TrimCandidate(
                blob.StoreKey, length, [.. entitled.Select(reference => reference.Ref)]));
            eligibleBytes += length;
        }

        var lines = new List<string>
        {
            $"trimmable: {eligible.Count} historic data blob(s), {eligibleBytes} byte(s)",
        };
        if (heldBack > 0)
        {
            lines.Add($"trim held back: {heldBack} data blob(s) awaiting a verified copy at every destination");
        }

        foreach (var (name, reason) in blocked)
        {
            lines.Add($"trim: destination '{name}' {Explain(reason)}");
        }

        return new TrimPlan(eligible, eligibleBytes, heldBack, lines)
        {
            PublicationSequence = maxPublicationSequence,
        };
    }

    /// <summary>Deletes the plan's eligible blobs from staging.</summary>
    /// <remarks>
    /// The plan's evidence is re-checked at the moment of deletion for every
    /// destination whose store can be asked directly: the tombstone and
    /// sweep phases run between planning and this pass, and a replica is a
    /// directory anything may touch — a copy that was there when the plan
    /// was made and is gone now means the blob stays. The proof stamp is
    /// re-read too. A synced sequence only ever advances, but a proof does
    /// not: a verification that failed between planning and here withdraws
    /// the licence to delete, and the blob stays for the next pass to
    /// re-plan.
    /// </remarks>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="verificationFor">The same verifications the plan was built from.</param>
    /// <param name="syncRecordFor">The sync ledger, re-read per candidate so a withdrawn proof is seen.</param>
    /// <param name="cancellationToken">Stops the deletes; a re-run re-plans from scratch.</param>
    /// <returns>
    /// What actually went — a blob already gone, or no longer verified, counts
    /// nothing — and, named, whatever the store refused to release.
    /// </returns>
    public static async ValueTask<(int Deleted, long Bytes, IReadOnlyList<string> Findings)> ExecuteAsync(
        IObjectStore store,
        TrimPlan plan,
        Func<string, TrimVerification> verificationFor,
        Func<string, DestinationSyncRecord?> syncRecordFor,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(verificationFor);
        ThrowHelper.ThrowIfNull(syncRecordFor);

        var deleted = 0;
        var bytes = 0L;
        var findings = new List<string>();
        foreach (var candidate in plan.Eligible)
        {
            var verified = true;
            foreach (var name in candidate.EntitledDestinations)
            {
                var proven = Proves(syncRecordFor(name), plan.PublicationSequence);
                verified = verificationFor(name) switch
                {
                    TrimVerification.StoreProbe probe => proven
                        && await HoldsAsync(probe.Replica, candidate.StoreKey, cancellationToken)
                            .ConfigureAwait(false),
                    TrimVerification.LedgerClaim => proven,
                    _ => false,
                };

                if (!verified)
                {
                    break;
                }
            }

            if (!verified)
            {
                continue;
            }

            DeleteResult outcome;
            try
            {
                outcome = await store.DeleteAsync(candidate.StoreKey, DeleteConditions.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file the platform will not release right now — Windows
                // holds a sharing violation over a blob mid-read — stays for
                // the next pass; one stubborn file must not abort the trim,
                // let alone the sets still waiting behind this one. Deferring
                // is correct; deferring silently is not, because the report
                // would then be indistinguishable from one where the trim had
                // nothing to do.
                findings.Add(
                    $"deferred: historic blob {candidate.StoreKey} could not be trimmed — {exception.Message}");
                continue;
            }

            if (outcome.Outcome == DeleteOutcome.Deleted)
            {
                deleted++;
                bytes += candidate.Length;
            }
            else if (outcome.Outcome != DeleteOutcome.NotFound)
            {
                findings.Add(
                    $"deferred: historic blob {candidate.StoreKey} was refused by the store ({outcome.Outcome})");
            }
        }

        return (deleted, bytes, findings);
    }

    /// <summary>Why one destination stalled the trim.</summary>
    private enum TrimHold
    {
        /// <summary>Declared unprovable in configuration — nothing will change on its own.</summary>
        Declared,

        /// <summary>Unreachable or of an unsupported kind — a later pass may find it.</summary>
        Unreachable,

        /// <summary>Reachable, but it has not proven what it was last sent.</summary>
        Unproven,

        /// <summary>Proven and reachable, but its store does not hold this blob.</summary>
        DoesNotHold,
    }

    /// <summary>
    /// The operator-facing half of each hold. Only the reasons that genuinely
    /// clear on their own say "right now"; a declared-unprovable destination
    /// names the three ways out instead, because waiting is not one of them.
    /// </summary>
    private static string Explain(TrimHold reason) => reason switch
    {
        TrimHold.Declared =>
            "is declared unprovable, so staging keeps its history — fix it so it can be verified, "
            + "remove it, or give it retention rules so it is not entitled to the history at all",
        TrimHold.Unreachable => "cannot vouch for its copies right now",
        TrimHold.Unproven => "has not proven what it was last sent",
        _ => "does not hold every historic blob staging would drop",
    };

    /// <summary>
    /// Whether a destination has PROVEN, recently enough to matter, that it
    /// holds real bytes at or beyond this pass's sequence (FR-VER-006).
    /// </summary>
    /// <param name="record">The pair's sync ledger row, or null when never attempted.</param>
    /// <param name="maxPublicationSequence">The sequence the trim is reasoning about.</param>
    /// <remarks>
    /// Freshness is expressed without a new configuration knob: the last
    /// <b>successful</b> sync must itself have proven something. Since every
    /// sync now verifies, a success recorded without a stamp leaves
    /// <c>LastSuccessAt</c> ahead of <c>VerifiedAt</c> — and that gap is
    /// exactly the case where the destination's latest state is unproven, so
    /// it must not license a delete. A destination excused from proving never
    /// satisfies this at all: refusing to prove and authorising deletion are
    /// not both available.
    /// </remarks>
    private static bool Proves(DestinationSyncRecord? record, ulong maxPublicationSequence) =>
        record is { VerifiedAt: { } verifiedAt, LastSuccessAt: { } lastSuccess }
        && record.VerifiedObjects > 0
        && record.VerifiedSequence >= maxPublicationSequence
        && verifiedAt >= lastSuccess;

    private static async ValueTask<bool> HoldsAsync(
        IObjectStore replica, ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await replica.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            return metadata.Metadata is { Length: > 0 };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever a store's fault looks like — an unplugged drive's
            // IOException today, a provider's auth or transport failure
            // later — the answer is the same: this copy cannot be vouched
            // for, and the fault of one destination must never abort the
            // whole multi-set retention pass.
            return false;
        }
    }

    private static TrimPlan Nothing(string line) => new([], 0, 0, [line]);
}
