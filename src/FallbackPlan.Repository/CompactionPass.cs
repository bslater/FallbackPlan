using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Resources;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository;

/// <summary>The ordered steps of one compaction pass (ADR-0067; 08 §3.1, §5).</summary>
public enum CompactionStep
{
    /// <summary>0 — nothing is durable yet. Not a step: what a failure carries when the pass died choosing.</summary>
    Preparing = 0,

    /// <summary>1 — the compaction write intent is durable.</summary>
    PublishIntent = 1,

    /// <summary>2 — the candidates are read and their live records sealed into new blobs, still local.</summary>
    SealBlobs = 2,

    /// <summary>3 — every produced blob is durable, each covered by its own extension before its put.</summary>
    UploadBlobs = 3,

    /// <summary>4 — the supersessions are published and applied; readers now resolve to the new blobs.</summary>
    PublishIndex = 4,

    /// <summary>5 — retirement, after the index and never before it.</summary>
    RetireIntent = 5,

    /// <summary>6 — the pass is complete.</summary>
    Complete = 6,
}

/// <summary>Observes step completion, so a test can cut a pass where it likes.</summary>
public interface ICompactionObserver
{
    /// <summary>Called once each step is durable.</summary>
    /// <param name="completedStep">The step that just finished.</param>
    void AfterStep(CompactionStep completedStep);
}

/// <summary>What one compaction pass did.</summary>
/// <param name="Published">The blobs it produced and uploaded.</param>
/// <param name="Drained">The source blobs whose live records now live elsewhere.</param>
/// <param name="DeltaIds">The deltas carrying the supersessions.</param>
/// <param name="IntentSequence">The journal sequence of its write intent.</param>
/// <param name="RecordsMoved">How many sealed records were relocated.</param>
public sealed record CompactionOutcome(
    IReadOnlyList<BlobId> Published,
    IReadOnlyList<BlobId> Drained,
    IReadOnlyList<DeltaId> DeltaIds,
    ulong IntentSequence,
    long RecordsMoved)
{
    /// <summary>A pass that had nothing to do.</summary>
    public static CompactionOutcome Nothing { get; } = new([], [], [], 0, 0);
}

/// <summary>
/// One compaction pass, in the order that makes an interruption safe
/// ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md); ADR-0025 exit
/// criteria 4, 7 and 12).
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole of it, and every step is one the engine already
/// had: the intent is durable before a blob byte moves, each blob is covered
/// by its own extension before its put, the supersessions are published, and
/// only then is the intent retired. Cut anywhere before the last step and the
/// produced blobs are covered by an unretired intent, so a collector running
/// concurrently treats them as reachable (08 §8, FR-GC-003) rather than as
/// the unreferenced garbage they otherwise look exactly like; the drained
/// blobs are untouched, so nothing a reader needs has moved.
/// </para>
/// <para>
/// Retiring before publishing would invert that. The window between
/// retirement and the delta landing is one in which the produced blobs are
/// covered by nothing and named by nothing — a collector may delete them,
/// and the delta then names blobs no store holds, which is ADR-0025 exit
/// criterion 12 exactly.
/// </para>
/// <para>
/// It deletes nothing. The drained blobs are condemned by the collector on
/// its own terms — every record they hold resolves elsewhere now, so the
/// ordinary plan finds them dead — and the tombstone's grace and the sweep's
/// revalidation apply to them as to any other blob. A compactor that deleted
/// its own sources would be asserting a conclusion the collector is built to
/// reach.
/// </para>
/// </remarks>
public static class CompactionPass
{
    /// <summary>
    /// Compacts the chosen candidates and moves the index onto the result.
    /// </summary>
    /// <param name="candidates">The blobs to drain, with the live records worth carrying.</param>
    /// <param name="repository">The opened archive — its effective format version decides whether this is possible at all.</param>
    /// <param name="source">Where the candidates are read from.</param>
    /// <param name="destination">Where the produced blobs are put — the archive store, or a direct-ship set's sink.</param>
    /// <param name="writerId">This device's writer identity.</param>
    /// <param name="policy">Sizing and profile for the blobs produced.</param>
    /// <param name="sequence">The writer's sequence — blob counters and journal numbers come from it.</param>
    /// <param name="catalogue">The catalogue the published deltas are applied to.</param>
    /// <param name="spoolDirectory">Where produced blobs spool before they are sealed.</param>
    /// <param name="nowUnixMilliseconds">Informational stamp for the journal records.</param>
    /// <param name="declaredMaxDurationMs">How long the intent claims it may take (08 §4).</param>
    /// <param name="expiryGeneration">The generation past which the intent is stale.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <param name="observer">Sees each step complete.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="deltaByteBudget">Overrides the delta size budget; tests only.</param>
    /// <returns>What the pass did.</returns>
    /// <exception cref="InvalidOperationException">The repository is below format 3, where relocation is impossible.</exception>
    public static async ValueTask<CompactionOutcome> RunAsync(
        IReadOnlyList<CompactionSource> candidates,
        OpenedRepository repository,
        IObjectStore source,
        IObjectStore destination,
        WriterId writerId,
        CapturePolicy policy,
        WriterSequence sequence,
        Catalogue.Catalogue catalogue,
        string spoolDirectory,
        ulong nowUnixMilliseconds,
        ulong declaredMaxDurationMs,
        uint expiryGeneration,
        CancellationToken cancellationToken,
        ICompactionObserver? observer = null,
        ILogger? logger = null,
        int? deltaByteBudget = null)
    {
        ThrowHelper.ThrowIfNull(candidates);
        ThrowHelper.ThrowIfNull(repository);
        ThrowHelper.ThrowIfNull(source);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(sequence);
        ThrowHelper.ThrowIfNull(catalogue);

        if (!FormatVersions.HasRelocatableRecords(repository.EffectiveFormatVersion))
        {
            throw new InvalidOperationException(
                Strings.FormatCompactionPass_NeedsFormatThree(repository.EffectiveFormatVersion));
        }

        if (candidates.Count == 0)
        {
            return CompactionOutcome.Nothing;
        }

        var generation = Math.Max(
            repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);

        using var journal = new JournalPublisher(
            destination, repository.RepositoryId, writerId, repository.Credential, sequence, logger);

        // Step 1: the intent, durable before a blob byte moves. Purpose
        // Compaction, because a collector is a writer too and 08 §3.2 makes
        // no exception for maintenance.
        var intentSequence = await journal.PublishAsync(
            JournalRecordKind.WriteIntent,
            new JournalPayload.WriteIntent(
                repository.RepositoryId.ToArray(), [], declaredMaxDurationMs, expiryGeneration,
                IntentPurpose.Compaction),
            nowUnixMilliseconds,
            generation,
            cancellationToken).ConfigureAwait(false);

        var scope = new ExtensionIntentScope(
            journal, intentSequence, declaredMaxDurationMs, nowUnixMilliseconds, generation);
        observer?.AfterStep(CompactionStep.PublishIntent);

        // Step 2: the rewrite. No key that could open a record is constructed
        // anywhere below this line.
        IReadOnlyList<CompactedBlob> produced;
        using (var compactor = new BlobCompactor(
            repository.RepositoryId, writerId, new KeyGeneration(generation), repository.Keys, policy,
            source, sequence, spoolDirectory, repository.EffectiveFormatVersion, logger))
        {
            produced = await compactor.CompactAsync(candidates, cancellationToken).ConfigureAwait(false);
        }

        observer?.AfterStep(CompactionStep.SealBlobs);

        try
        {
            using var keyDeriver = new StoreBlobKeyDeriver(repository.Keys.KeyIdKey);
            using var publisher = new IndexPublisher(
                destination, repository.RepositoryId, writerId, repository.Credential, sequence, logger);

            // Steps 3 and 4. They are one call because the publication's own
            // order — every blob durable before any entry names one — is the
            // half of criterion 12 that lives inside it. The seam between
            // them is real and the observer is told about it there, not
            // afterwards: it is the window the retirement must not be moved
            // into, and a cut that cannot land in it proves nothing.
            var published = await CompactionPublication.PublishAsync(
                produced, destination, keyDeriver, publisher, catalogue, generation, cancellationToken,
                deltaByteBudget, scope, sequence,
                afterUploads: () => observer?.AfterStep(CompactionStep.UploadBlobs)).ConfigureAwait(false);

            observer?.AfterStep(CompactionStep.PublishIndex);

            // Step 5: retirement — after the index, never before it.
            await journal.PublishAsync(
                JournalRecordKind.IntentRetirement,
                new JournalPayload.IntentRetirement(intentSequence, IntentOutcome.Completed),
                nowUnixMilliseconds,
                generation,
                cancellationToken).ConfigureAwait(false);
            observer?.AfterStep(CompactionStep.RetireIntent);

            var moved = produced.Sum(blob => (long)blob.Superseding.Count);
            Log.CompactionComplete(
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                published.Published.Count,
                published.Drained.Count,
                moved);

            observer?.AfterStep(CompactionStep.Complete);
            return new CompactionOutcome(
                published.Published, published.Drained, published.DeltaIds, intentSequence, moved);
        }
        finally
        {
            foreach (var blob in produced)
            {
                await blob.Sealed.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Covers blobs by intent-extension (08 §4): the extension naming a blob
    /// is durable before that blob is uploaded, each extension independently.
    /// </summary>
    private sealed class ExtensionIntentScope(
        JournalPublisher journal,
        ulong intentSequence,
        ulong declaredMaxDurationMs,
        ulong nowUnixMilliseconds,
        uint generation) : IIntentScope
    {
        private readonly HashSet<BlobId> _covered = [];

        public async ValueTask EnsureCoveredAsync(BlobId blobId, CancellationToken cancellationToken)
        {
            if (!_covered.Add(blobId))
            {
                return;
            }

            await journal.PublishAsync(
                JournalRecordKind.IntentExtension,
                new JournalPayload.IntentExtension(intentSequence, [blobId], declaredMaxDurationMs),
                nowUnixMilliseconds,
                generation,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
