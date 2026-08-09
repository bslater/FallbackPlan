using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Index;

/// <summary>Whether an entry adds a location or supersedes one (specification 07 §2.1 position 5).</summary>
public enum IndexEntryType : byte
{
    /// <summary>An additional, equally valid location — two insertions for one object are benign (07 §3).</summary>
    Insertion = 1,

    /// <summary>A relocation that MUST be honoured in order — compaction's product (07 §3; ADR-0017).</summary>
    Supersession = 2,
}

/// <summary>
/// One index entry (specification 07 §2.1): the six-element array mapping an
/// object identifier to its physical location. The wire form packs the two
/// profiles into one u32 (compression high, encryption low); in memory they
/// stay separate.
/// </summary>
public sealed record IndexEntry(
    ObjectId ObjectId,
    BlobId BlobId,
    ulong PhysicalOffset,
    uint StoredLength,
    ushort CompressionProfileValue,
    ushort EncryptionProfileValue,
    IndexEntryType EntryType)
{
    /// <summary>The packed wire form of position 4: compression in the high 16 bits, encryption in the low 16.</summary>
    public uint PackedProfiles => ((uint)CompressionProfileValue << 16) | EncryptionProfileValue;

    /// <summary>
    /// The entry's index shard — the top 16 bits of the object identifier
    /// (specification 07 §8). Distinct from the blob-path shard of 01 §2.
    /// </summary>
    public ushort Shard
    {
        get
        {
            Span<byte> bytes = stackalloc byte[32];
            ObjectId.CopyTo(bytes);
            return (ushort)((bytes[0] << 8) | bytes[1]);
        }
    }

    /// <summary>Unpacks position 4's wire form.</summary>
    public static (ushort Compression, ushort Encryption) UnpackProfiles(uint packed) =>
        ((ushort)(packed >> 16), (ushort)(packed & 0xFFFF));
}

/// <summary>
/// An entry with its precedence sort keys attached (specification 07 §3's
/// note made structural): generation, writer, and sequence live on the
/// <em>enclosing</em> delta or checkpoint, not in the entry array, so any
/// in-memory entry must carry its provenance or precedence cannot be
/// decided.
/// </summary>
public sealed record ProvenancedEntry(
    IndexEntry Entry,
    ulong Generation,
    WriterId WriterId,
    ulong Sequence);

/// <summary>What a reader knows about a blob's lifecycle (specification 07 §3 rule 3).</summary>
public enum BlobState
{
    /// <summary>Live or unknown — the default assumption.</summary>
    Live = 1,

    /// <summary>Tombstoned pending deletion.</summary>
    Tombstoned = 2,

    /// <summary>Known deleted.</summary>
    Deleted = 3,
}

/// <summary>
/// The 07 §3 precedence rules, verbatim and in order: highest generation
/// wins; at equal generation the higher <c>(writer_id, sequence)</c> wins
/// (writer compared as unsigned bytes, then sequence numerically); an entry
/// whose blob is known deleted is treated as superseded even if it wins,
/// and reported as a damage finding. With precedence explicit, applying
/// entries in any order converges on the same state — the winner is a
/// property of the entries, not of arrival sequence (07 §6).
/// </summary>
public static class IndexPrecedence
{
    /// <summary>
    /// Resolves the winning location for one object identifier from every
    /// candidate entry, in any order.
    /// </summary>
    /// <param name="candidates">Every entry seen for one object identifier.</param>
    /// <param name="blobState">The reader's knowledge of blob lifecycles.</param>
    /// <param name="findings">Receives the rule-3 damage findings.</param>
    /// <returns>The winning entry, or null when every candidate points into a deleted blob.</returns>
    public static ProvenancedEntry? Resolve(
        IReadOnlyList<ProvenancedEntry> candidates,
        Func<BlobId, BlobState> blobState,
        ICollection<DamageFinding> findings)
    {
        ThrowHelper.ThrowIfNull(candidates);
        ThrowHelper.ThrowIfNull(blobState);
        ThrowHelper.ThrowIfNull(findings);

        ProvenancedEntry? winner = null;

        foreach (var candidate in candidates)
        {
            if (winner is null || Compare(candidate, winner) > 0)
            {
                winner = candidate;
            }
        }

        if (winner is null)
        {
            return null;
        }

        // Rule 3: a winner naming a deleted blob is superseded regardless,
        // and the anomaly is reported — an entry that won on generation but
        // points nowhere is exactly the state 07 §3 calls a damage finding.
        if (blobState(winner.Entry.BlobId) == BlobState.Deleted)
        {
            findings.Add(new DamageFinding(
                DamageKind.MissingBlob,
                $"The winning index entry for object {winner.Entry.ObjectId} names deleted blob {winner.Entry.BlobId}; treated as superseded (specification 07 §3 rule 3)."));

            var survivors = candidates
                .Where(candidate => blobState(candidate.Entry.BlobId) != BlobState.Deleted)
                .ToList();

            return survivors.Count == 0 ? null : Resolve(survivors, blobState, findings);
        }

        return winner;
    }

    /// <summary>Rules 1 and 2 as a total order: positive when <paramref name="left"/> wins.</summary>
    public static int Compare(ProvenancedEntry left, ProvenancedEntry right)
    {
        ThrowHelper.ThrowIfNull(left);
        ThrowHelper.ThrowIfNull(right);

        var byGeneration = left.Generation.CompareTo(right.Generation);
        if (byGeneration != 0)
        {
            return byGeneration;
        }

        Span<byte> leftWriter = stackalloc byte[16];
        Span<byte> rightWriter = stackalloc byte[16];
        left.WriterId.CopyTo(leftWriter);
        right.WriterId.CopyTo(rightWriter);

        var byWriter = leftWriter.SequenceCompareTo(rightWriter);
        if (byWriter != 0)
        {
            return byWriter;
        }

        return left.Sequence.CompareTo(right.Sequence);
    }
}
