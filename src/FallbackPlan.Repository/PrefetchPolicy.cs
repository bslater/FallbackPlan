namespace FallbackPlan.Repository;

/// <summary>
/// What a coalesced read may fetch, waste and hold (NFR-PERF-009,
/// NFR-PERF-001). A file's records sit next to each other in the blob they
/// were written to, so merging their reads is what brings a restore under the
/// GET budget — and a merge with no bounds is how a request budget gets paid
/// for in bandwidth and memory instead.
/// </summary>
/// <remarks>
/// Named and passed rather than written as three literals in the reader,
/// because the three are a policy with a trade in it, and because a test
/// cannot otherwise reach the cases the bounds exist for: a blob large enough
/// to hold a megabyte-wide gap is not a blob a suite should have to write.
/// </remarks>
public sealed record PrefetchPolicy
{
    /// <summary>The bounds every restore uses unless a caller says otherwise.</summary>
    public static PrefetchPolicy Default { get; } = new();

    /// <summary>
    /// The most one coalesced read may fetch. NFR-PERF-001 bounds memory by
    /// configuration rather than by what a file happens to be, so a large
    /// file becomes several runs rather than one enormous buffer.
    /// </summary>
    public long CoalesceWindowBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// The most one coalesced read may <em>waste</em>. A gap between two
    /// needed records is bridged only when it is smaller than this, and a run
    /// reaches down to offset 0 for the envelope only when the first record
    /// it wants is within it. Without the bound, restoring one late record
    /// from a 128 MiB blob would fetch the whole blob to save a request — a
    /// GET budget bought with a bandwidth bill.
    /// </summary>
    public long MaximumBridgeBytes { get; init; } = 1L * 1024 * 1024;

    /// <summary>
    /// The most one prefetch may hold at once across every blob it touched. A
    /// plan can name more manifests than any machine should buffer, so runs
    /// are taken until this is reached and whatever is left reads one record
    /// at a time exactly as it did before — slower, and bounded.
    /// </summary>
    public long BudgetBytes { get; init; } = 64L * 1024 * 1024;
}
