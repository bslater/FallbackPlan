using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Catalogue;

/// <summary>The outcome of a catalogue rebuild.</summary>
public sealed record RebuildReport(
    int DeltasApplied,
    int CheckpointsApplied,
    int LocationsRecorded,
    IReadOnlyList<DamageFinding> Findings);

/// <summary>
/// Rebuilds the catalogue from checkpoint plus deltas (E1; FR-MAN-006,
/// FR-MAN-009): delete the file, load the index plane, re-apply. The
/// catalogue never holds anything the store cannot regenerate — this type is
/// the proof.
/// </summary>
public sealed class CatalogueRebuilder
{
    private readonly IndexLoader _loader;

    /// <summary>Creates a rebuilder over a loader.</summary>
    public CatalogueRebuilder(IndexLoader loader)
    {
        ThrowHelper.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>
    /// Builds the blob-lifecycle view specification 07 §3 rule 3 needs, from
    /// an inventory of the blobs a store actually holds.
    /// </summary>
    /// <param name="present">Every blob the store was observed to hold.</param>
    /// <param name="inventoryComplete">
    /// Whether that observation can be trusted to be exhaustive — false when
    /// any blob could not be opened, because an inventory with holes cannot
    /// distinguish "collected" from "unreadable right now".
    /// </param>
    /// <returns>A lifecycle lookup safe to hand to the loader.</returns>
    /// <remarks>
    /// Absence is evidence of deletion only when the inventory is exhaustive.
    /// Everywhere else this answers <see cref="BlobState.Live"/>, which the
    /// enum documents as "live or unknown — the default assumption": a listing
    /// with holes, on a provider that lists eventually, must never cause valid
    /// locations to be discarded and reported as damage. Refusing to serve a
    /// location is cheap to get wrong in the loud direction and expensive in
    /// the quiet one.
    /// </remarks>
    public static Func<BlobId, BlobState> KnownBlobs(IEnumerable<BlobId> present, bool inventoryComplete)
    {
        ThrowHelper.ThrowIfNull(present);

        if (!inventoryComplete)
        {
            return _ => BlobState.Live;
        }

        var held = present.ToHashSet();
        return blobId => held.Contains(blobId) ? BlobState.Live : BlobState.Deleted;
    }

    /// <summary>
    /// Loads the index plane and re-applies it into
    /// <paramref name="target"/>, recording every finding the load surfaced.
    /// </summary>
    /// <param name="target">The catalogue to rebuild into.</param>
    /// <param name="currentGeneration">The repository's current generation.</param>
    /// <param name="gapPatienceGenerations">How long a sequence gap may stay unresolved before it is damage.</param>
    /// <param name="isSequenceAccountedAsync">Accounts sequences the index cannot see.</param>
    /// <param name="cancellationToken">Cancels the rebuild.</param>
    /// <param name="blobState">
    /// What the caller knows about blob lifecycles, for precedence rule 3 —
    /// see <see cref="KnownBlobs"/>. Omitting it assumes every blob is live,
    /// which makes rule 3 unreachable: an entry that wins on generation while
    /// naming a blob the store no longer holds is then served as though it
    /// were a location.
    /// </param>
    public async ValueTask<RebuildReport> RebuildAsync(
        Catalogue target,
        ulong currentGeneration,
        int gapPatienceGenerations,
        Func<WriterId, ulong, ValueTask<bool>>? isSequenceAccountedAsync,
        CancellationToken cancellationToken,
        Func<BlobId, BlobState>? blobState = null)
    {
        ThrowHelper.ThrowIfNull(target);

        var state = await _loader.LoadAsync(
            currentGeneration, gapPatienceGenerations, isSequenceAccountedAsync, blobState, cancellationToken)
            .ConfigureAwait(false);

        // Checkpoints first, then deltas — though order cannot matter
        // (07 §6): the SQL resolver re-derives precedence from provenance
        // columns, not from arrival.
        var checkpointsApplied = 0;
        foreach (var checkpoint in state.Checkpoints)
        {
            // The store path carries the checkpoint id; the loader does not
            // retain it, so rebuild allocates ledger identities from the
            // content — deterministic enough for idempotence within one
            // rebuild pass, and the ledger is itself disposable cache.
            var checkpointId = CheckpointId.FromBytes(
                System.Security.Cryptography.SHA256.HashData(checkpoint.SignedBytes.Span).AsSpan(0, 16));
            target.ApplyCheckpoint(checkpointId, checkpoint.Checkpoint);
            checkpointsApplied++;
        }

        var deltasApplied = 0;
        foreach (var delta in state.Deltas)
        {
            var deltaId = DeltaId.FromBytes(
                System.Security.Cryptography.SHA256.HashData(delta.SignedBytes.Span).AsSpan(0, 16));
            target.ApplyDelta(deltaId, delta.Delta);
            deltasApplied++;
        }

        // The lifecycle conclusion is recorded, not just acted on: a rebuild
        // that silently declined to serve a location leaves the next reader to
        // work out why from nothing. With the state stored, the catalogue can
        // answer "that blob is gone" instead of "no such object".
        if (blobState is { } lifecycle)
        {
            foreach (var blobId in state.AllEntries.Select(entry => entry.Entry.BlobId).Distinct())
            {
                if (lifecycle(blobId) is var known && known != BlobState.Live)
                {
                    target.SetBlobState(blobId, known);
                }
            }
        }

        foreach (var finding in state.Findings)
        {
            target.RecordFinding(finding);
        }

        target.SetSource("checkpoint-rebuild");

        return new RebuildReport(deltasApplied, checkpointsApplied, state.AllEntries.Count, state.Findings);
    }
}
