using System.Globalization;
using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>One blob the plan would delete whole.</summary>
/// <param name="StoreKey">The blob's store key.</param>
/// <param name="BlobId">Its writer-allocated identity — what a tombstone names (spec 11 §3).</param>
/// <param name="Records">How many records it holds — all unreachable, or it would not be here.</param>
public sealed record DeletableBlob(ObjectKey StoreKey, BlobId BlobId, long Records);

/// <summary>
/// What one collection pass would do — produced before anything is done,
/// because the dry-run report is mandatory (FR-GC-005). The pass itself
/// deletes: a blob with a live minority stays whole and is reported as the
/// compaction backlog, and a blob compaction has already drained is garbage
/// here like any other, because every record it holds now resolves
/// elsewhere (ADR-0067).
/// </summary>
/// <param name="ProtectedSnapshots">The snapshots treated as protected, with the planner's reasons.</param>
/// <param name="ExpiredSnapshotKeys">The standalone snapshot objects the pass would remove.</param>
/// <param name="DeletableBlobs">Blobs holding nothing reachable <em>here</em> and nothing intent-covered — including one compaction drained, whose records the index now resolves into another blob.</param>
/// <param name="PartlyLiveBlobs">Blobs kept whole for a live minority — the compaction backlog, named rather than counted so a pass can act on it (ADR-0067).</param>
/// <param name="Vetoes">
/// Conditions that force this pass to delete nothing at all: an undecodable
/// snapshot, an unwalkable manifest, a blob the reader had to skip. Damage
/// means the object graph cannot be trusted to say what is garbage.
/// </param>
public sealed record CollectionPlan(
    IReadOnlyList<SnapshotKeep> ProtectedSnapshots,
    IReadOnlyList<ObjectKey> ExpiredSnapshotKeys,
    IReadOnlyList<DeletableBlob> DeletableBlobs,
    IReadOnlyList<CompactableBlob> PartlyLiveBlobs,
    IReadOnlyList<string> Vetoes)
{
    /// <summary>Whether the pass may delete anything at all.</summary>
    public bool Deletable => Vetoes.Count == 0;
}

/// <summary>
/// Steps 1–5 of the collection algorithm (architecture 07 §3): mark from the
/// protected snapshots, add every blob an unretired write intent covers
/// (step 4 — ADR-0009's reason to exist), and plan the difference, counting
/// a record as living in a blob only while the index still sends readers
/// there. Anything doubtful vetoes the whole pass rather than narrowing
/// it: a collector that guesses is the failure mode this design was built
/// against.
/// </summary>
public static class CollectionPlanner
{
    /// <summary>Builds the plan.</summary>
    /// <param name="survey">The store's snapshots (staging is the only place this runs — ADR-0009 Amendment 4).</param>
    /// <param name="selection">The policy's verdict over those snapshots.</param>
    /// <param name="gate">The replication gate's verdict over the expire-set (FR-GC-009).</param>
    /// <param name="reader">The footer-truth reader, blobs loaded.</param>
    /// <param name="reachable">The mark set from <see cref="StagingMark.MarkAsync"/>.</param>
    /// <param name="unwalkable">Objects the mark could not read — each one a veto.</param>
    /// <param name="intents">The journal's live-intent survey — step 4's input.</param>
    /// <param name="resolveLocation">
    /// Where the index says an object now lives, or null when it has no
    /// opinion. Supplying it is what lets a pass condemn a blob compaction
    /// drained: a record whose bytes are still here but whose <b>location</b>
    /// is another blob is dead here, and a blob every one of whose records is
    /// dead here is garbage like any other (07 §3, ADR-0067). Omit it and the
    /// planner behaves as it did before compaction existed — conservatively,
    /// since a drained blob then simply stays.
    /// </param>
    /// <returns>The plan, never yet an action.</returns>
    public static CollectionPlan Plan(
        SnapshotSurvey survey,
        RetentionSelection selection,
        GateResult gate,
        RepositoryReader reader,
        HashSet<ObjectId> reachable,
        IReadOnlyList<string> unwalkable,
        IntentSurvey intents,
        Func<ObjectId, BlobId?>? resolveLocation = null)
    {
        ThrowHelper.ThrowIfNull(survey);
        ThrowHelper.ThrowIfNull(selection);
        ThrowHelper.ThrowIfNull(gate);
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(reachable);
        ThrowHelper.ThrowIfNull(unwalkable);
        ThrowHelper.ThrowIfNull(intents);

        var vetoes = new List<string>();
        foreach (var undecodable in survey.Undecodable)
        {
            vetoes.Add($"snapshot object would not decode: {undecodable}");
        }

        foreach (var failure in unwalkable)
        {
            vetoes.Add($"protected closure would not walk: {failure}");
        }

        foreach (var skipped in reader.SkippedBlobs)
        {
            vetoes.Add($"blob would not open: {skipped.Key} — {skipped.Reason}");
        }

        // Only snapshots BOTH expired by policy AND released by the gate go;
        // a held snapshot's whole closure was already marked reachable by the
        // caller walking keep ∪ held.
        var expirable = gate.Expirable.Select(fact => fact.SnapshotId).ToHashSet(StringComparer.Ordinal);
        var expiredKeys = survey.Snapshots
            .Where(snapshot => expirable.Contains(snapshot.Fact.SnapshotId))
            .Select(snapshot => snapshot.StoreKey)
            .ToList();

        // A supersession may only condemn a record here if the blob the index
        // sends readers to is one this reader actually opened. Trusting an
        // entry that names a blob nobody holds would delete the last copy of
        // a record on the strength of a pointer into nothing — ADR-0025 exit
        // criterion 12, inverted and fatal.
        var present = reader.Blobs.Select(blob => blob.BlobId).ToHashSet();

        var deletable = new List<DeletableBlob>();
        var partlyLive = new List<CompactableBlob>();
        foreach (var (storeKey, blobId, records) in reader.Blobs)
        {
            // Step 4: an unretired intent's coverage is reachability, no
            // exceptions, no heuristics (FR-GC-003). A blob a live intent
            // covers is never a compaction candidate either — another writer
            // may still be appending to it (ADR-0008).
            if (intents.IsCovered(blobId))
            {
                continue;
            }

            var live = records.Where(record => LivesHere(record, blobId)).ToList();
            if (live.Count == records.Count)
            {
                continue;
            }

            if (live.Count > 0)
            {
                // Stored lengths, summed: what a rewrite would carry and what
                // it would leave behind. The footer and the per-record framing
                // go with the old blob either way, so they are not counted on
                // either side of the trade.
                var liveBytes = live.Sum(record => (long)record.StoredLength);
                var allBytes = records.Sum(record => (long)record.StoredLength);
                partlyLive.Add(new CompactableBlob(
                    storeKey, blobId, live, liveBytes, allBytes - liveBytes, records.Count - live.Count));
                continue;
            }

            deletable.Add(new DeletableBlob(storeKey, blobId, records.Count));
        }

        return new CollectionPlan(
            selection.Keep,
            expiredKeys,
            deletable,
            partlyLive,
            vetoes);

        bool LivesHere(RecordTableEntry record, BlobId blobId)
        {
            if (!reachable.Contains(record.ObjectId))
            {
                return false;
            }

            return resolveLocation?.Invoke(record.ObjectId) is not { } winner
                || winner.Equals(blobId)
                || !present.Contains(winner);
        }
    }

    /// <summary>The dry-run report, in the order a human reads it (FR-GC-005).</summary>
    /// <param name="plan">The plan to describe.</param>
    /// <param name="held">The gate's held snapshots, laggards named.</param>
    /// <returns>Report lines.</returns>
    public static IReadOnlyList<string> Describe(CollectionPlan plan, IReadOnlyList<HeldSnapshot> held)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(held);

        var lines = new List<string>
        {
            $"protected: {plan.ProtectedSnapshots.Count} snapshot(s)",
        };

        foreach (var keep in plan.ProtectedSnapshots)
        {
            lines.Add($"  keep {keep.Snapshot.SnapshotId[..12]}… — {string.Join(", ", keep.Reasons)}");
        }

        foreach (var holding in held)
        {
            lines.Add(
                $"  held {holding.Snapshot.SnapshotId[..12]}… — awaiting {string.Join(", ", holding.AwaitingDestinations)}"
                + (holding.DeferralExceeded ? " (deferral bound exceeded — needs action)" : string.Empty));
        }

        lines.Add($"would delete: {plan.ExpiredSnapshotKeys.Count} snapshot object(s), {plan.DeletableBlobs.Count} blob(s)");
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"kept whole for a live minority: {plan.PartlyLiveBlobs.Count} blob(s), "
            + $"{plan.PartlyLiveBlobs.Sum(blob => blob.DeadBytes):N0} dead byte(s) in them"));

        foreach (var veto in plan.Vetoes)
        {
            lines.Add($"NO DELETION — {veto}");
        }

        return lines;
    }
}
