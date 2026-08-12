using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Retention;

/// <summary>One retention pass's report.</summary>
/// <param name="Lines">The dry-run report, always produced (FR-GC-005).</param>
/// <param name="Held">The gate's holds, for the caller to raise deferral warnings from.</param>
/// <param name="TombstonesWritten">Tombstones written this pass — 0 on a dry run.</param>
/// <param name="Swept">The sweep outcome — null on a dry run.</param>
public sealed record RetentionReport(
    IReadOnlyList<string> Lines,
    IReadOnlyList<HeldSnapshot> Held,
    int TombstonesWritten,
    SweepOutcome? Swept);

/// <summary>
/// One set's retention pass, whole (architecture 07): survey the store,
/// select by policy, gate on replication, mark the protected closure, plan
/// against footers and live intents — and, only when asked and only when
/// nothing vetoed, tombstone the condemned and sweep what an earlier pass
/// condemned whose grace has run. The dry-run report is produced either way,
/// because it is mandatory before any destructive pass (FR-GC-005).
/// </summary>
public static class RetentionRunner
{
    /// <summary>Runs one pass against one staging archive.</summary>
    /// <param name="store">The staging archive's store.</param>
    /// <param name="repository">The opened archive.</param>
    /// <param name="policy">The set's retention policy — absent means nothing expires.</param>
    /// <param name="destinations">The set's declared destination references, overrides included (FR-GC-010).</param>
    /// <param name="syncRecordFor">The sync-ledger row for a destination (FR-GC-009's input).</param>
    /// <param name="trimVerificationFor">How each destination's holdings can be verified for the staging trim (ADR-0034 §6).</param>
    /// <param name="writerId">This device's writer identity, for tombstones.</param>
    /// <param name="apply">False: report only. True: tombstone, sweep and trim under the full gate.</param>
    /// <param name="nowUnixMilliseconds">The clock — policy windows and informational stamps only.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The report.</returns>
    public static async ValueTask<RetentionReport> RunAsync(
        IObjectStore store,
        OpenedRepository repository,
        RetentionConfiguration? policy,
        IReadOnlyList<SetDestinationReference> destinations,
        Func<string, DestinationSyncRecord?> syncRecordFor,
        Func<string, TrimVerification> trimVerificationFor,
        WriterId writerId,
        bool apply,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(store);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(destinations);
        ThrowHelper.ThrowIfNull(syncRecordFor);
        ThrowHelper.ThrowIfNull(trimVerificationFor);

        var survey = await StagingMark.SurveyAsync(store, repository, cancellationToken).ConfigureAwait(false);

        var selection = RetentionPlanner.Select(
            [.. survey.Snapshots.Select(snapshot => snapshot.Fact)],
            policy ?? new RetentionConfiguration(),
            DateTimeOffset.FromUnixTimeMilliseconds((long)nowUnixMilliseconds));

        // Per-destination keep-awareness (FR-GC-010): a destination whose own
        // policy drops a snapshot never holds it, so it never holds up its
        // expiry from staging either. Each override's keep-set is computed
        // over the same facts the set-level selection used.
        var facts = survey.Snapshots.Select(snapshot => snapshot.Fact).ToList();
        var keptByDestination = destinations.ToDictionary(
            reference => reference.Ref,
            reference =>
            {
                var effective = reference.Retention ?? policy;
                if (!DestinationConvergence.HasRules(effective))
                {
                    return null;
                }

                return RetentionPlanner
                    .Select(facts, effective!, DateTimeOffset.FromUnixTimeMilliseconds((long)nowUnixMilliseconds))
                    .Keep.Select(keep => keep.Snapshot.SnapshotId)
                    .ToHashSet(StringComparer.Ordinal);
            },
            StringComparer.Ordinal);

        var gate = ReplicationGate.Apply(
            selection.Expire,
            [.. destinations.Select(reference => reference.Ref)],
            syncRecordFor,
            keptBy: (name, snapshot) =>
                keptByDestination.GetValueOrDefault(name) is not { } kept || kept.Contains(snapshot.SnapshotId),
            policy?.DeferralDays,
            nowUnixMilliseconds);

        // The mark walks keep ∪ held: a snapshot the gate is holding must
        // keep its whole closure — it is still the only copy somewhere.
        var protectedIds = selection.Keep.Select(keep => keep.Snapshot.SnapshotId)
            .Concat(gate.Held.Select(held => held.Snapshot.SnapshotId))
            .ToHashSet(StringComparer.Ordinal);
        var protectedSnapshots = survey.Snapshots
            .Where(snapshot => protectedIds.Contains(snapshot.Fact.SnapshotId))
            .ToList();

        using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, protectedSnapshots, cancellationToken)
            .ConfigureAwait(false);

        var sealingGeneration = Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);
        IReadOnlyList<JournalRecord> records;
        int unparseable;
        using (var journal = new JournalReader(store, repository.RepositoryId, repository.Hierarchy))
        {
            (records, unparseable, _) = await journal.LoadAsync(sealingGeneration, cancellationToken)
                .ConfigureAwait(false);
        }

        var intents = IntentSurveyor.Survey(
            records, unparseable, sealingGeneration, nowUnixMilliseconds, skewMarginMs: 300_000);

        var plan = CollectionPlanner.Plan(survey, selection, gate, reader, reachable, unwalkable, intents);
        var lines = new List<string>(CollectionPlanner.Describe(plan, gate.Held));

        // The trim decides either way — the dry run must say what would go
        // (FR-GC-005) — and deletes only under apply, after the sweep.
        var trim = await StagingTrim.PlanAsync(
            store, reader, survey, policy, destinations, trimVerificationFor, syncRecordFor, intents,
            DateTimeOffset.FromUnixTimeMilliseconds((long)nowUnixMilliseconds), cancellationToken)
            .ConfigureAwait(false);
        lines.AddRange(trim.Lines);

        if (!apply)
        {
            return new RetentionReport(lines, gate.Held, 0, null);
        }

        // The grace clock: the single writer's highest published sequence.
        var publicationSequence = records.Count == 0 ? 0 : records.Max(record => record.Sequence);

        var written = 0;
        if (plan.Deletable && (plan.DeletableBlobs.Count > 0 || plan.ExpiredSnapshotKeys.Count > 0))
        {
            written = await StagingSweep.TombstoneAsync(
                store, repository, writerId, plan, survey, publicationSequence, nowUnixMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            lines.Add($"tombstoned: {written} object(s), eligible after the next publication");
        }

        // The sweep acts only on tombstones whose grace has run — this
        // pass's own tombstones never qualify, and earlier passes' are
        // revalidated against the world just computed (11 §3.2 step 3).
        var swept = await StagingSweep.SweepAsync(
            store, repository, plan, survey, publicationSequence, cancellationToken).ConfigureAwait(false);
        lines.Add(
            $"swept: {swept.Deleted} deleted, {swept.NotYetEligible} awaiting grace, "
            + $"{swept.TombstonesCleared} tombstone(s) cleared");
        lines.AddRange(swept.Findings);

        // The trim runs last: direct deletes of historic data blobs every
        // entitled destination verifiably holds (ADR-0034 §6). A blob the
        // sweep already removed counts nothing here.
        if (trim.Eligible.Count > 0)
        {
            var (trimmed, bytes) = await StagingTrim.ExecuteAsync(store, trim, cancellationToken)
                .ConfigureAwait(false);
            lines.Add($"trimmed: {trimmed} historic data blob(s), {bytes} byte(s)");
        }

        return new RetentionReport(lines, gate.Held, written, swept);
    }
}
