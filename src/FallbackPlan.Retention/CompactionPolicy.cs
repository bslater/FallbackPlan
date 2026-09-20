using System.Globalization;
using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention;

/// <summary>
/// One blob the collector kept whole for a live minority — the compaction
/// backlog, named rather than counted (ADR-0067). It carries the live
/// records themselves, because the rewrite copies exactly those and would
/// otherwise have to read the footer a second time to learn which.
/// </summary>
/// <param name="StoreKey">The blob's store key.</param>
/// <param name="BlobId">Its writer-allocated identity — what a tombstone names (spec 11 §3).</param>
/// <param name="Live">The records a protected snapshot still reaches, in the order the blob holds them.</param>
/// <param name="LiveBytes">What those records occupy, summed over their stored lengths.</param>
/// <param name="DeadBytes">What the rest occupy — what a rewrite would reclaim.</param>
/// <param name="DeadRecords">How many records are unreachable.</param>
public sealed record CompactableBlob(
    ObjectKey StoreKey,
    BlobId BlobId,
    IReadOnlyList<RecordTableEntry> Live,
    long LiveBytes,
    long DeadBytes,
    long DeadRecords);

/// <summary>
/// Which of the backlog a pass rewrites (ADR-0067). Two bounds, for two
/// different reasons: a <b>fraction</b>, because rewriting a blob that is
/// mostly live moves many bytes to reclaim few; and a <b>floor in bytes</b>,
/// because the cost of a rewrite is in bytes and a small blob is not worth
/// it whatever its fraction says. A third bound caps the pass itself, so a
/// long-neglected archive converges over several runs rather than
/// monopolising one (NFR-OPS-008's rule, applied to work rather than to
/// state).
/// </summary>
/// <param name="DeadFraction">The share of a blob that must be dead before it is worth rewriting.</param>
/// <param name="MinimumReclaim">The bytes a single blob must reclaim before it is worth rewriting.</param>
/// <param name="ByteBudget">The most one pass reads, summed over the blobs it takes.</param>
public sealed record CompactionPolicy(double DeadFraction, long MinimumReclaim, long ByteBudget)
{
    /// <summary>Half dead, four mebibytes reclaimed, two hundred and fifty-six read in a pass.</summary>
    public static CompactionPolicy Default { get; } = new(0.5, 4L * 1024 * 1024, 256L * 1024 * 1024);

    /// <summary>
    /// The blobs this pass would rewrite, largest reclaim first so that a
    /// budgeted pass buys the most space it can, and never past the budget.
    /// </summary>
    /// <param name="backlog">Every blob the collector kept whole for a live minority.</param>
    /// <returns>The candidates, in the order a pass should take them.</returns>
    public IReadOnlyList<CompactableBlob> SelectCandidates(IReadOnlyList<CompactableBlob> backlog)
    {
        ThrowHelper.ThrowIfNull(backlog);

        var taken = new List<CompactableBlob>();
        long budget = 0;

        foreach (var blob in backlog.Where(Eligible).OrderByDescending(blob => blob.DeadBytes))
        {
            // The pass is bounded by what it READS, which is the whole
            // candidate — live bytes and dead alike — because the rewrite
            // copies the live ones out of a blob it has had to open.
            var cost = blob.LiveBytes + blob.DeadBytes;
            if (budget + cost > ByteBudget)
            {
                continue;
            }

            budget += cost;
            taken.Add(blob);
        }

        return taken;
    }

    /// <summary>The dry-run lines for the compaction phase (FR-GC-005).</summary>
    /// <param name="backlog">Every blob kept whole for a live minority.</param>
    /// <param name="candidates">What <see cref="SelectCandidates"/> chose from it.</param>
    /// <returns>Report lines, empty when there is no backlog at all.</returns>
    public IReadOnlyList<string> Describe(
        IReadOnlyList<CompactableBlob> backlog, IReadOnlyList<CompactableBlob> candidates)
    {
        ThrowHelper.ThrowIfNull(backlog);
        ThrowHelper.ThrowIfNull(candidates);

        if (backlog.Count == 0)
        {
            return [];
        }

        var reclaim = candidates.Sum(blob => blob.DeadBytes);
        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"compaction would rewrite: {candidates.Count} blob(s), reclaiming {reclaim:N0} byte(s)"),
        };

        var waiting = backlog.Count - candidates.Count;
        if (waiting > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {waiting} blob(s) left whole: below the threshold ({DeadFraction:P0} dead and "
                + $"{MinimumReclaim:N0} byte(s) reclaimed), or past this pass's budget of "
                + $"{ByteBudget:N0} byte(s) read"));
        }

        return lines;
    }

    private bool Eligible(CompactableBlob blob)
    {
        var total = blob.LiveBytes + blob.DeadBytes;
        return total > 0
            && blob.DeadBytes >= MinimumReclaim
            && blob.DeadBytes >= (long)(total * DeadFraction);
    }
}
