namespace FallbackPlan.Storage.Abstractions;

/// <summary>What a provider can promise about listing freshness.</summary>
public enum ListingConsistency
{
    /// <summary>The provider makes no statement.</summary>
    Unknown,

    /// <summary>Listings may lag writes.</summary>
    Eventual,

    /// <summary>Listings reflect completed writes.</summary>
    Strong,
}

/// <summary>
/// A provider's capabilities, probed once and reported separately from the
/// data path (docs/architecture/05-storage-providers.md §3) so capability
/// checks never sit inside a hot loop and no provider behaviour leaks into
/// snapshot semantics (NFR-COMP-005, NFR-PORT-004).
/// </summary>
public sealed record StoreCapabilities
{
    /// <summary>Whether create-if-absent is honoured.</summary>
    public bool ConditionalCreate { get; init; }

    /// <summary>Whether conditional replacement is honoured.</summary>
    public bool ConditionalReplace { get; init; }

    /// <summary>Whether ranged reads are served; without them restore fetches whole blobs.</summary>
    public bool RangedReads { get; init; }

    /// <summary>Whether multipart upload exists.</summary>
    public bool MultipartUpload { get; init; }

    /// <summary>Whether deletes can be batched.</summary>
    public bool BatchDelete { get; init; }

    /// <summary>Whether the store versions objects.</summary>
    public bool ObjectVersioning { get; init; }

    /// <summary>Whether object-lock (immutability enforcement) exists.</summary>
    public bool ObjectLock { get; init; }

    /// <summary>Whether the provider computes server-side checksums.</summary>
    public bool ServerSideChecksums { get; init; }

    /// <summary>The listing-freshness promise.</summary>
    public ListingConsistency ListingConsistency { get; init; }

    /// <summary>The minimum storage duration before early-deletion charges, where billed.</summary>
    public TimeSpan? MinimumStorageDuration { get; init; }

    /// <summary>Whether archival tiers (rehydration latency) exist.</summary>
    public bool ArchivalTiers { get; init; }

    /// <summary>The largest object the provider accepts, in bytes.</summary>
    public long MaximumObjectSize { get; init; }

    /// <summary>The largest per-object metadata the provider accepts, in bytes.</summary>
    public int MaximumMetadataBytes { get; init; }

    /// <summary>
    /// What a writer fanning out to several stores at once may promise: the
    /// weakest answer each of them gives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A store that stands in front of others — <c>Agent/DestinationShipSink</c>
    /// is the one this exists for — cannot honestly forward any single
    /// member's capabilities, because a caller that acts on the promise acts
    /// against all of them. Conditional create is only usable if every target
    /// honours it; the largest object that can be written is the smallest any
    /// of them accepts; a listing is only as fresh as the laggiest.
    /// </para>
    /// <para>
    /// <see cref="ArchivalTiers"/> is the one member that is <b>or</b>ed rather
    /// than <b>and</b>ed, and the asymmetry is deliberate rather than a slip:
    /// it is a hazard, not a promise. It says rehydration latency applies.
    /// Intersecting it away would let a fan-out claim that nothing it writes
    /// to archives, when one of its targets does, which is the opposite of the
    /// conservative answer every other member is giving.
    /// </para>
    /// <para>
    /// An empty sequence answers <see langword="null"/>: a fan-out with no
    /// targets resolved has nothing to promise less than its caller already
    /// knows, and inventing a floor would be a statement nobody made.
    /// </para>
    /// </remarks>
    /// <param name="capabilities">What each store promises.</param>
    /// <returns>The weakest of them, or <see langword="null"/> when there are none.</returns>
    public static StoreCapabilities? Intersect(IEnumerable<StoreCapabilities> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        StoreCapabilities? weakest = null;
        foreach (var next in capabilities)
        {
            ArgumentNullException.ThrowIfNull(next);

            if (weakest is null)
            {
                weakest = next;
                continue;
            }

            weakest = new StoreCapabilities
            {
                ConditionalCreate = weakest.ConditionalCreate && next.ConditionalCreate,
                ConditionalReplace = weakest.ConditionalReplace && next.ConditionalReplace,
                RangedReads = weakest.RangedReads && next.RangedReads,
                MultipartUpload = weakest.MultipartUpload && next.MultipartUpload,
                BatchDelete = weakest.BatchDelete && next.BatchDelete,
                ObjectVersioning = weakest.ObjectVersioning && next.ObjectVersioning,
                ObjectLock = weakest.ObjectLock && next.ObjectLock,
                ServerSideChecksums = weakest.ServerSideChecksums && next.ServerSideChecksums,

                // Unknown < Eventual < Strong, which is the order the enum is
                // declared in — so the weakest promise is the lower value.
                ListingConsistency = (ListingConsistency)Math.Min(
                    (int)weakest.ListingConsistency, (int)next.ListingConsistency),

                // The longest wait before an early delete is billed: a caller
                // planning around it must satisfy every target.
                MinimumStorageDuration = Longer(weakest.MinimumStorageDuration, next.MinimumStorageDuration),

                ArchivalTiers = weakest.ArchivalTiers || next.ArchivalTiers,
                MaximumObjectSize = Math.Min(weakest.MaximumObjectSize, next.MaximumObjectSize),
                MaximumMetadataBytes = Math.Min(weakest.MaximumMetadataBytes, next.MaximumMetadataBytes),
            };
        }

        return weakest;

        static TimeSpan? Longer(TimeSpan? left, TimeSpan? right) =>
            left is null ? right
            : right is null ? left
            : left > right ? left : right;
    }
}
