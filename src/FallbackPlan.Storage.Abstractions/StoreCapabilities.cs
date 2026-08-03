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
}
