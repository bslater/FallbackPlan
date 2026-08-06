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
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>
    /// Loads the index plane and re-applies it into
    /// <paramref name="target"/>, recording every finding the load surfaced.
    /// </summary>
    public async ValueTask<RebuildReport> RebuildAsync(
        Catalogue target,
        ulong currentGeneration,
        int gapPatienceGenerations,
        Func<WriterId, ulong, ValueTask<bool>>? isSequenceAccountedAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = await _loader.LoadAsync(
            currentGeneration, gapPatienceGenerations, isSequenceAccountedAsync, blobState: null, cancellationToken)
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

        foreach (var finding in state.Findings)
        {
            target.RecordFinding(finding);
        }

        target.SetSource("checkpoint-rebuild");

        return new RebuildReport(deltasApplied, checkpointsApplied, state.AllEntries.Count, state.Findings);
    }
}
