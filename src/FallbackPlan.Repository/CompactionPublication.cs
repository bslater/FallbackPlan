using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Resources;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository;

/// <summary>
/// One delta a compaction pass would publish: the blobs it covers, their
/// commitments, and the entries that move readers onto them.
/// </summary>
/// <param name="CoveredBlobIds">The blobs whose records these entries live in (07 §2.1 key 6).</param>
/// <param name="CoveredBlobDigests">Their flat digests, parallel (07 §2.2).</param>
/// <param name="CoveredBlobMerkleRoots">Their Merkle commitments, parallel (07 §2.3).</param>
/// <param name="Entries">The supersessions.</param>
public sealed record CompactionDelta(
    IReadOnlyList<BlobId> CoveredBlobIds,
    IReadOnlyList<ReadOnlyMemory<byte>> CoveredBlobDigests,
    IReadOnlyList<ReadOnlyMemory<byte>> CoveredBlobMerkleRoots,
    IReadOnlyList<IndexEntry> Entries);

/// <summary>What one compaction pass published.</summary>
/// <param name="DeltaIds">The deltas, in the order they were published.</param>
/// <param name="Published">The blobs uploaded.</param>
/// <param name="Drained">The source blobs their records came out of.</param>
public sealed record PublishedCompaction(
    IReadOnlyList<DeltaId> DeltaIds,
    IReadOnlyList<BlobId> Published,
    IReadOnlyList<BlobId> Drained);

/// <summary>
/// Uploads what <see cref="BlobCompactor"/> produced and moves the index onto
/// it ([ADR-0067](../../docs/adr/0067-the-keyless-compactor.md)).
/// </summary>
/// <remarks>
/// <para>
/// The entries are <b>supersessions</b> (07 §2 key 5, ADR-0017): a
/// relocation is ordered, not a second location either of which would
/// serve. This is the first thing in the product ever to publish one —
/// both capture paths construct insertions. What actually moves a reader
/// off the drained blob is 07 §3's precedence, which is generation then
/// <c>(writer_id, sequence)</c> and does not read the entry type; the type
/// states the intent that ordering carries out, and a compaction entry
/// always carries the higher sequence.
/// </para>
/// <para>
/// A pass's entries are split across deltas so that no delta exceeds
/// <see cref="FormatLimits.MaxMetadataObjectSize"/>. That bound is real and
/// nothing else enforces it for a delta: manifests, tree manifests and blob
/// footers all check it, <c>IndexDeltaCodec</c> does not, and a delta written
/// over it is refused at <b>read</b> by the standalone-record framing — so an
/// unsplit pass would write an object that nothing rejects until the day it
/// is needed. The split lives here rather than in the publisher because an
/// ordinary publication's delta is bounded by what one capture wrote, while a
/// compaction pass hands over a whole byte budget's worth of blobs at once.
/// </para>
/// <para>
/// It does not tombstone the drained blobs and does not write a journal
/// intent. Both are the pass's, with an ordering that makes an interrupted
/// compaction safe, and burying them here would bury the order.
/// </para>
/// </remarks>
public static class CompactionPublication
{
    /// <summary>Headroom left for the delta's own header, signature and framing.</summary>
    public const int DeltaHeaderMargin = 64 * 1024;

    /// <summary>A conservative per-entry cost: identifiers, location, profiles and CBOR framing.</summary>
    private const int EntryEstimate = 96;

    /// <summary>A conservative per-covered-blob cost: identifier, digest, Merkle root and framing.</summary>
    private const int CoveredEstimate = 128;

    /// <summary>The default budget for one delta's encoded bytes.</summary>
    public static int DefaultDeltaByteBudget => FormatLimits.MaxMetadataObjectSize - DeltaHeaderMargin;

    /// <summary>
    /// Groups the produced blobs into deltas, each within
    /// <paramref name="byteBudget"/>.
    /// </summary>
    /// <remarks>
    /// The split is at blob boundaries, so a blob is covered by exactly one
    /// delta and no covered list ever names it twice. A single blob cannot
    /// overflow a delta on its own — at most
    /// <see cref="FormatLimits.MaxRecordsPerBlob"/> entries, which is far
    /// inside the bound — and a blob that somehow did is refused by name
    /// rather than published into an object nothing can read.
    /// </remarks>
    /// <param name="produced">What the compactor sealed.</param>
    /// <param name="byteBudget">The most one delta's encoded form may occupy.</param>
    /// <returns>The deltas to publish, in order.</returns>
    /// <exception cref="InvalidOperationException">One blob's entries alone exceed the budget.</exception>
    public static IReadOnlyList<CompactionDelta> Split(
        IReadOnlyList<CompactedBlob> produced, int byteBudget)
    {
        ThrowHelper.ThrowIfNull(produced);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);

        var deltas = new List<CompactionDelta>();
        var ids = new List<BlobId>();
        var digests = new List<ReadOnlyMemory<byte>>();
        var roots = new List<ReadOnlyMemory<byte>>();
        var entries = new List<IndexEntry>();
        var cost = 0;

        foreach (var blob in produced)
        {
            var blobCost = CoveredEstimate + (blob.Superseding.Count * EntryEstimate);
            if (blobCost > byteBudget)
            {
                throw new InvalidOperationException(
                    Strings.FormatCompactionPublication_BlobExceedsDeltaBudget(
                        blob.Sealed.BlobId, blob.Superseding.Count, byteBudget));
            }

            if (entries.Count > 0 && cost + blobCost > byteBudget)
            {
                deltas.Add(new CompactionDelta([.. ids], [.. digests], [.. roots], [.. entries]));
                ids.Clear();
                digests.Clear();
                roots.Clear();
                entries.Clear();
                cost = 0;
            }

            ids.Add(blob.Sealed.BlobId);
            digests.Add(blob.Sealed.Digest.ToArray());
            roots.Add(blob.Sealed.MerkleRoot.ToArray());
            entries.AddRange(blob.Superseding);
            cost += blobCost;
        }

        if (entries.Count > 0)
        {
            deltas.Add(new CompactionDelta([.. ids], [.. digests], [.. roots], [.. entries]));
        }

        return deltas;
    }

    /// <summary>
    /// Uploads the produced blobs, publishes the supersessions, and applies
    /// them to the catalogue.
    /// </summary>
    /// <param name="produced">What the compactor sealed; each is disposed by the caller.</param>
    /// <param name="destination">Where the blobs are put — an archive store, or a direct-ship set's sink.</param>
    /// <param name="storeKeys">Derives each blob's store key from its identity.</param>
    /// <param name="publisher">The index publisher for this writer.</param>
    /// <param name="catalogue">The catalogue to apply the deltas to.</param>
    /// <param name="generation">The publication generation.</param>
    /// <param name="cancellationToken">Cancels the uploads and publications.</param>
    /// <param name="deltaByteBudget">Overrides <see cref="DefaultDeltaByteBudget"/>; for tests that need a split without sixteen mebibytes of entries.</param>
    /// <returns>What was published.</returns>
    /// <exception cref="IOException">A blob's store key already held different bytes.</exception>
    public static async ValueTask<PublishedCompaction> PublishAsync(
        IReadOnlyList<CompactedBlob> produced,
        IObjectStore destination,
        StoreBlobKeyDeriver storeKeys,
        IndexPublisher publisher,
        Catalogue.Catalogue catalogue,
        ulong generation,
        CancellationToken cancellationToken,
        int? deltaByteBudget = null)
    {
        ThrowHelper.ThrowIfNull(produced);
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(storeKeys);
        ThrowHelper.ThrowIfNull(publisher);
        ThrowHelper.ThrowIfNull(catalogue);

        if (produced.Count == 0)
        {
            return new PublishedCompaction([], [], []);
        }

        // Blobs first, then the index that names them: an entry resolving
        // into a blob nobody uploaded is the one shape 07 §3 rule 3 reports
        // as damage, and the only way to avoid producing it is the order.
        foreach (var blob in produced)
        {
            await UploadAsync(destination, storeKeys, blob.Sealed, cancellationToken).ConfigureAwait(false);
        }

        var deltaIds = new List<DeltaId>();
        foreach (var chunk in Split(produced, deltaByteBudget ?? DefaultDeltaByteBudget))
        {
            var (deltaId, delta) = await publisher.PublishDeltaDetailedAsync(
                generation,
                chunk.CoveredBlobIds,
                chunk.Entries,
                chunk.CoveredBlobDigests,
                chunk.CoveredBlobMerkleRoots,
                cancellationToken).ConfigureAwait(false);

            catalogue.ApplyDelta(deltaId, delta);
            deltaIds.Add(deltaId);
        }

        return new PublishedCompaction(
            deltaIds,
            [.. produced.Select(blob => blob.Sealed.BlobId)],
            [.. produced.SelectMany(blob => blob.Drained).Distinct()]);
    }

    private static async ValueTask UploadAsync(
        IObjectStore destination,
        StoreBlobKeyDeriver storeKeys,
        SealedBlob blob,
        CancellationToken cancellationToken)
    {
        var key = BlobStoreKeys.ForBlob(blob.BlobClass, storeKeys.Derive(blob.BlobId));
        var put = await destination.PutAsync(
            key, blob.OpenContentAsync, PutConditions.IfNotExists, cancellationToken).ConfigureAwait(false);

        if (put.Outcome == PutOutcome.PreconditionFailed)
        {
            throw new IOException(Strings.FormatCompactionPublication_StoreRefusedBlob(key));
        }

        // This identifier was freshly allocated, so anything already under
        // its key means the sequence state regressed and another run's bytes
        // are there — the same reasoning the archive session's upload makes,
        // and the same refusal.
        if (put.Outcome == PutOutcome.AlreadyExists &&
            !await SealedBlobReadback.MatchesAsync(destination, key, blob, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException(Strings.FormatCompactionPublication_StoreHeldDifferentBytes(key));
        }
    }
}
