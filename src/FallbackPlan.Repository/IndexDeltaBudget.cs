using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>One blob a delta covers, with the entries that locate records in it.</summary>
/// <param name="BlobId">The blob (07 §2 key 6).</param>
/// <param name="Digest">Its flat digest (07 §2.2).</param>
/// <param name="MerkleRoot">Its Merkle commitment (07 §2.3); null below format 3, which publishes none.</param>
/// <param name="Entries">The entries that point into it.</param>
public sealed record CoveredBlob(
    BlobId BlobId, ReadOnlyMemory<byte> Digest, ReadOnlyMemory<byte>? MerkleRoot, IReadOnlyList<IndexEntry> Entries);

/// <summary>One delta's share of a publication, in the parallel arrays a delta carries.</summary>
/// <param name="CoveredBlobIds">The blobs (07 §2 key 6).</param>
/// <param name="CoveredBlobDigests">Their flat digests, parallel (07 §2.2).</param>
/// <param name="CoveredBlobMerkleRoots">Their Merkle commitments, parallel, or empty where none is published (07 §2.3).</param>
/// <param name="Entries">The entries.</param>
public sealed record IndexDeltaPart(
    IReadOnlyList<BlobId> CoveredBlobIds,
    IReadOnlyList<ReadOnlyMemory<byte>> CoveredBlobDigests,
    IReadOnlyList<ReadOnlyMemory<byte>> CoveredBlobMerkleRoots,
    IReadOnlyList<IndexEntry> Entries);

/// <summary>
/// How much one index delta may hold, and the cut that keeps every delta
/// inside it (specification 07 §2, 00 §8; FR-MAN-008).
/// </summary>
/// <remarks>
/// <para>
/// A delta is a standalone metadata object, and the framing refuses one over
/// <see cref="FormatLimits.MaxMetadataObjectSize"/> at read, before a byte of
/// it is allocated. A capture writes as many records as its files need, and
/// a compaction pass a byte budget's worth of blobs, so neither bounds its
/// entries by the limit on its own. Both cut here.
/// </para>
/// <para>
/// The cut is at blob boundaries, so a blob is covered by exactly one delta,
/// beside its digest and root, and no covered list names it twice. A single
/// blob holds at most <see cref="FormatLimits.MaxRecordsPerBlob"/> records,
/// far inside the budget, and one that somehow did not is refused by name
/// rather than published into an object nothing can read.
/// </para>
/// </remarks>
public static class IndexDeltaBudget
{
    /// <summary>Headroom left for the delta's own header, signature and framing.</summary>
    public const int HeaderMargin = 64 * 1024;

    // Conservative wire costs: an entry's identifiers, location, profiles and
    // CBOR framing come to about 70 bytes, a covered blob's identifier, digest
    // and root to about 90.
    private const int EntryEstimate = 96;
    private const int CoveredEstimate = 128;

    /// <summary>The budget for one delta's encoded bytes.</summary>
    public static int Default => FormatLimits.MaxMetadataObjectSize - HeaderMargin;

    /// <summary>Cuts a publication's covered blobs into deltas, each within <paramref name="byteBudget"/>.</summary>
    /// <param name="blobs">The blobs, in the order their entries are to be published; each carries a Merkle root, or none does.</param>
    /// <param name="byteBudget">The most one delta's encoded form may occupy.</param>
    /// <returns>The deltas to publish, in order; empty for no blobs.</returns>
    /// <exception cref="InvalidOperationException">One blob's entries alone exceed the budget.</exception>
    public static IReadOnlyList<IndexDeltaPart> Split(IReadOnlyList<CoveredBlob> blobs, int byteBudget)
    {
        ThrowHelper.ThrowIfNull(blobs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);

        var parts = new List<IndexDeltaPart>();
        var ids = new List<BlobId>();
        var digests = new List<ReadOnlyMemory<byte>>();
        var roots = new List<ReadOnlyMemory<byte>>();
        var entries = new List<IndexEntry>();
        var cost = 0;

        foreach (var blob in blobs)
        {
            var blobCost = CoveredEstimate + (blob.Entries.Count * EntryEstimate);
            if (blobCost > byteBudget)
            {
                throw new InvalidOperationException(
                    Strings.FormatIndexDeltaBudget_BlobExceedsDeltaBudget(blob.BlobId, blob.Entries.Count, byteBudget));
            }

            if (ids.Count > 0 && cost + blobCost > byteBudget)
            {
                parts.Add(new IndexDeltaPart([.. ids], [.. digests], [.. roots], [.. entries]));
                ids.Clear();
                digests.Clear();
                roots.Clear();
                entries.Clear();
                cost = 0;
            }

            ids.Add(blob.BlobId);
            digests.Add(blob.Digest);
            if (blob.MerkleRoot is { } root)
            {
                roots.Add(root);
            }

            entries.AddRange(blob.Entries);
            cost += blobCost;
        }

        if (ids.Count > 0)
        {
            parts.Add(new IndexDeltaPart([.. ids], [.. digests], [.. roots], [.. entries]));
        }

        return parts;
    }
}
