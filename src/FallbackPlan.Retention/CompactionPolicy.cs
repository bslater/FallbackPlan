using System.Globalization;
using Bodu;
using FallbackPlan.Domain;
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
/// What one pass would rewrite, and what it would say about it.
/// </summary>
/// <param name="Candidates">The blobs to hand a compaction pass, in the order to take them.</param>
/// <param name="Lines">The dry-run lines, empty when there is nothing to say (FR-GC-005).</param>
public sealed record CompactionSelection(
    IReadOnlyList<CompactableBlob> Candidates, IReadOnlyList<string> Lines);

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
    /// What this pass would rewrite and what it would say — the whole
    /// decision in one call, so the dry run and the act cannot be computed
    /// twice and disagree (FR-GC-005).
    /// </summary>
    /// <param name="effectiveFormatVersion">
    /// The repository's effective format ([ADR-0066](../../docs/adr/0066-the-format-upgrade-record.md)).
    /// Below format 3 a rewrite is decrypt-and-reseal, which needs a content
    /// key this service does not hold (FR-WOR-003), so nothing is selected.
    /// </param>
    /// <param name="backlog">Every blob the collector kept whole for a live minority.</param>
    /// <returns>The candidates and the lines.</returns>
    public CompactionSelection Select(ushort effectiveFormatVersion, IReadOnlyList<CompactableBlob> backlog)
    {
        ThrowHelper.ThrowIfNull(backlog);

        var candidates = SelectCandidates(backlog);

        if (FormatVersions.HasRelocatableRecords(effectiveFormatVersion))
        {
            return new CompactionSelection(candidates, Describe(backlog, candidates));
        }

        // Below format 3 the offer is made only when there is something to
        // offer. ADR-0066 decision 6 supports both formats and pushes
        // neither, and acknowledging the format-upgradable notice silences it
        // for good; a line repeating the offer on every pass would re-open
        // what that acknowledgement closed. So a set whose backlog would not
        // have been worth rewriting anyway hears nothing.
        if (candidates.Count == 0)
        {
            return new CompactionSelection([], []);
        }

        var reclaimable = candidates.Sum(blob => blob.DeadBytes).ToString("N0", CultureInfo.InvariantCulture);
        return new CompactionSelection(
            [],
            [
                $"compaction would reclaim {reclaimable} byte(s) here, and this set is at format "
                + $"{effectiveFormatVersion.ToString(CultureInfo.InvariantCulture)}: relocating a sealed record "
                + "is a format-3 property, and below it a rewrite needs a content key this service does not "
                + "hold. Run upgrade_set_format to make the next pass able to do it.",
            ]);
    }

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
