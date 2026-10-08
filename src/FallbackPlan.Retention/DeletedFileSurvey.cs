using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Retention;

/// <summary>
/// Two snapshots next to each other in the order the retention rules read
/// them (FR-GC-014): the plausible snapshots nobody asked to delete, newest
/// first.
/// </summary>
/// <param name="Older">The earlier of the two, which may hold a path the later one does not.</param>
/// <param name="Newer">The later, whose capture time dates a deletion found between them.</param>
public sealed record AdjacentSnapshots(SnapshotFact Older, SnapshotFact Newer);

/// <summary>
/// Which neighbouring snapshots lost a path, as <see cref="DeletedFileSurvey"/>
/// found them (FR-GC-014, ADR-0094). Only "lost nothing" is an answer: a
/// pair the survey found lost a path and a pair nobody compared both count
/// as one that did, because the rule they feed only ever keeps more.
/// </summary>
public sealed class DeletedFiles
{
    private readonly HashSet<(string Older, string Newer)> _nothingLost;

    private DeletedFiles(HashSet<(string Older, string Newer)> nothingLost) => _nothingLost = nothingLost;

    /// <summary>Nothing compared: every pair counts as one that lost a path.</summary>
    public static DeletedFiles Unknown { get; } = new([]);

    /// <summary>The pairs known to have lost nothing; every other pair counts as one that did.</summary>
    /// <param name="pairs">The pairs compared and found to have lost nothing.</param>
    public static DeletedFiles NoneBetween(IEnumerable<AdjacentSnapshots> pairs)
    {
        ThrowHelper.ThrowIfNull(pairs);
        return new([.. pairs.Select(pair => (pair.Older.SnapshotId, pair.Newer.SnapshotId))]);
    }

    /// <summary>Whether a path <paramref name="older"/> holds may be missing from <paramref name="newer"/>.</summary>
    /// <param name="older">The earlier snapshot of the pair.</param>
    /// <param name="newer">The later snapshot of the pair.</param>
    public bool Between(SnapshotFact older, SnapshotFact newer)
    {
        ThrowHelper.ThrowIfNull(older);
        ThrowHelper.ThrowIfNull(newer);
        return !_nothingLost.Contains((older.SnapshotId, newer.SnapshotId));
    }
}

/// <summary>
/// Finds which neighbouring snapshots lost a path (FR-GC-014, ADR-0094),
/// from the archive's own sealed trees. The catalogue is a cache, and what
/// retention keeps never hangs off a cache (architecture 07 §3).
/// </summary>
/// <remarks>
/// <para>
/// The newer of two snapshots lost a path when it has nothing at a path the
/// older holds, at any depth, or has something of another kind there: a
/// folder where a file was, or the reverse. Any path counts — a file, a
/// link, a folder, the old name of a rename, a path the rules stopped
/// capturing — and names compare as the bytes recorded, so a rename that
/// changes only case counts too. An edit or an addition loses nothing.
/// </para>
/// <para>
/// A subtree whose object is the same on both sides is the same subtree, and
/// is not read. A tree that will not read or decode counts as lost: what it
/// held cannot be known, and the answer that keeps more is the safe one.
/// </para>
/// </remarks>
public static class DeletedFileSurvey
{
    /// <summary>
    /// Compares the trees the deleted-file rule needs, for the longest
    /// duration in force across <paramref name="policies"/>, or compares
    /// nothing when none is.
    /// </summary>
    /// <param name="reader">The footer-truth reader, blobs already loaded.</param>
    /// <param name="snapshots">The surveyed snapshots, as the planner will see them.</param>
    /// <param name="policies">Every policy the answer will be handed to: a set's, and each destination's effective one.</param>
    /// <param name="now">The clock the policies evaluate against.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <param name="clockSkewMargin">As the planner is given it; a day when omitted.</param>
    /// <returns>The answers; <see cref="DeletedFiles.Unknown"/> when no policy reads them.</returns>
    public static async ValueTask<DeletedFiles> SurveyAsync(
        RepositoryReader reader,
        IReadOnlyList<SurveyedSnapshot> snapshots,
        IEnumerable<RetentionConfiguration?> policies,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        TimeSpan? clockSkewMargin = null)
    {
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNull(policies);

        var longest = LongestDuration(policies);
        if (longest == 0)
        {
            return DeletedFiles.Unknown;
        }

        var pairs = RetentionPlanner.DeletedFilePairs(
            [.. snapshots.Select(snapshot => snapshot.Fact)], longest, now, clockSkewMargin);
        return await CompareAsync(reader, snapshots, pairs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The longest deleted-file duration the planner would read across
    /// <paramref name="policies"/>, in days; 0 when it would read none, and
    /// there is nothing to compare.
    /// </summary>
    /// <remarks>
    /// A duration on a policy with no rule keeps nothing that policy does not
    /// already keep, so the planner never reads it there.
    /// </remarks>
    /// <param name="policies">A set's policy and each destination's effective one.</param>
    public static int LongestDuration(IEnumerable<RetentionConfiguration?> policies)
    {
        ThrowHelper.ThrowIfNull(policies);
        return policies
            .Where(policy => DestinationConvergence.HasRules(policy) && policy!.KeepDeletedDays > 0)
            .Select(policy => policy!.KeepDeletedDays!.Value)
            .DefaultIfEmpty()
            .Max();
    }

    /// <summary>Compares each pair's trees and answers which lost nothing.</summary>
    /// <param name="reader">The footer-truth reader, blobs already loaded.</param>
    /// <param name="snapshots">The surveyed snapshots the pairs are drawn from, for their root trees.</param>
    /// <param name="pairs">The pairs to compare, newest first as <see cref="RetentionPlanner.DeletedFilePairs"/> names them.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>The answers. A pair naming a snapshot the survey does not hold counts as one that lost a path.</returns>
    public static async ValueTask<DeletedFiles> CompareAsync(
        RepositoryReader reader,
        IReadOnlyList<SurveyedSnapshot> snapshots,
        IReadOnlyList<AdjacentSnapshots> pairs,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(reader);
        ThrowHelper.ThrowIfNull(snapshots);
        ThrowHelper.ThrowIfNull(pairs);

        var roots = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            roots[snapshot.Fact.SnapshotId] = snapshot.Manifest.RootTree;
        }

        // Trees are named by their content, so a tree read for one pair
        // answers for any other. Each pair's newer snapshot is the last pair's
        // older one, so the trees read on that side are carried over and the
        // rest let go: what is held is two snapshots' changed folders, never
        // the archive's.
        var carried = new Dictionary<ObjectId, Dictionary<string, TreeEntry>?>();
        var nothingLost = new List<AdjacentSnapshots>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(pair.Older.SnapshotId, out var olderRoot)
                || !roots.TryGetValue(pair.Newer.SnapshotId, out var newerRoot))
            {
                continue;
            }

            var olderSide = new Dictionary<ObjectId, Dictionary<string, TreeEntry>?>();
            var trees = new Trees(reader, olderSide, carried);
            if (!await LostAPathAsync(trees, olderRoot, newerRoot, cancellationToken).ConfigureAwait(false))
            {
                nothingLost.Add(pair);
            }

            carried = olderSide;
        }

        return DeletedFiles.NoneBetween(nothingLost);
    }

    private static async ValueTask<bool> LostAPathAsync(
        Trees trees, ObjectId olderTree, ObjectId newerTree, CancellationToken cancellationToken)
    {
        if (olderTree.Equals(newerTree))
        {
            return false;
        }

        var older = await trees.OlderAsync(olderTree, cancellationToken).ConfigureAwait(false);
        var newer = await trees.NewerAsync(newerTree, cancellationToken).ConfigureAwait(false);
        if (older is null || newer is null)
        {
            return true;
        }

        foreach (var (name, entry) in older)
        {
            if (!newer.TryGetValue(name, out var counterpart) || counterpart.EntryKind != entry.EntryKind)
            {
                return true;
            }

            if (entry.EntryKind == EntryKind.DirectoryPlaceholder
                && await LostAPathAsync(trees, entry.ObjectId, counterpart.ObjectId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One pair's tree reads: the older side's kept for the next pair, the
    /// newer side's served from what the last pair kept where it can be.
    /// </summary>
    private sealed class Trees(
        RepositoryReader reader,
        Dictionary<ObjectId, Dictionary<string, TreeEntry>?> olderSide,
        Dictionary<ObjectId, Dictionary<string, TreeEntry>?> carried)
    {
        private readonly Dictionary<ObjectId, Dictionary<string, TreeEntry>?> _newerSide = [];

        public async ValueTask<Dictionary<string, TreeEntry>?> OlderAsync(ObjectId treeId, CancellationToken cancellationToken)
        {
            if (olderSide.TryGetValue(treeId, out var known) || carried.TryGetValue(treeId, out known)
                || _newerSide.TryGetValue(treeId, out known))
            {
                olderSide[treeId] = known;
                return known;
            }

            return olderSide[treeId] = await EntriesAsync(reader, treeId, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<Dictionary<string, TreeEntry>?> NewerAsync(ObjectId treeId, CancellationToken cancellationToken)
        {
            if (carried.TryGetValue(treeId, out var known) || _newerSide.TryGetValue(treeId, out known)
                || olderSide.TryGetValue(treeId, out known))
            {
                return known;
            }

            return _newerSide[treeId] = await EntriesAsync(reader, treeId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A directory's entries by name, its whole chain of continuations
    /// included (specification 06 §9); null when any manifest in the chain
    /// will not read or decode, or the chain loops.
    /// </summary>
    private static async ValueTask<Dictionary<string, TreeEntry>?> EntriesAsync(
        RepositoryReader reader, ObjectId treeId, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        var visited = new HashSet<ObjectId>();
        for (ObjectId? next = treeId; next is { } manifest; )
        {
            if (!visited.Add(manifest))
            {
                return null;
            }

            var result = await reader.ReadSegmentAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != RecordReadOutcome.Ok || result.Plaintext is null)
            {
                return null;
            }

            TreeManifest tree;
            try
            {
                tree = TreeManifestCodec.Decode(result.Plaintext);
            }
            catch (FormatException)
            {
                return null;
            }

            foreach (var entry in tree.Entries)
            {
                entries[Convert.ToHexString(entry.Name.Span)] = entry;
            }

            next = tree.Continuation;
        }

        return entries;
    }
}
