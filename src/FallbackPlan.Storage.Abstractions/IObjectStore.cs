namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// The deliberately small store interface the repository engine depends on
/// (docs/architecture/05-storage-providers.md §2; FR-REP-002, NFR-PORT-004).
/// The engine assumes only put, get (whole or ranged), list-by-prefix, and
/// delete — never filesystem rename, strong listing consistency, provider
/// checksums, or mutable objects, each of which is absent from at least one
/// intended provider.
/// </summary>
/// <remarks>
/// Expected outcomes — already exists, not found, precondition failed — are
/// results; exceptions are reserved for genuine faults such as network,
/// authentication, or provider errors.
/// </remarks>
public interface IObjectStore
{
    /// <summary>The provider's probed capabilities.</summary>
    StoreCapabilities Capabilities { get; }

    /// <summary>Reports whether an object exists and its metadata.</summary>
    ValueTask<GetMetadataResult> GetMetadataAsync(
        ObjectKey key,
        CancellationToken cancellationToken);

    /// <summary>Opens an object, or a range of it, for reading.</summary>
    ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key,
        ObjectRange? range,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes an object. Content is a factory, not a <see cref="Stream"/>: a
    /// retry must be able to produce the content again, so the caller
    /// guarantees reproducibility and the provider may invoke the factory as
    /// many times as its retry policy requires
    /// (docs/architecture/05-storage-providers.md §2.1; NFR-REL-005).
    /// </summary>
    ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists objects under a prefix in ordinal key order. Continuation is
    /// owned by the enumerator; entries carry a resume token for callers that
    /// must persist a position across process restarts
    /// (docs/architecture/05-storage-providers.md §2.3).
    /// </summary>
    IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        CancellationToken cancellationToken);

    /// <summary>Deletes an object.</summary>
    ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key,
        DeleteConditions conditions,
        CancellationToken cancellationToken);
}
