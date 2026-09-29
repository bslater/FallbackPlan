using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// A store whose content reads and writes are paced through a destination's
/// transfer limit (NFR-PERF-013, ADR-0074): what <see cref="OpenReadAsync"/>
/// hands back and what <see cref="PutAsync"/> reads out of its content factory
/// are charged as they stream. Listings, metadata and deletes pass through
/// untouched — they move no content.
/// </summary>
/// <remarks>
/// Built only for background work, and only where a limit is declared; a
/// person's work is given the store itself, which is what
/// <see cref="Over"/> returns when there is no limiter.
/// </remarks>
internal sealed class PacedObjectStore : IObjectStore
{
    private readonly IObjectStore _inner;
    private readonly ByteRateLimiter _limiter;

    private PacedObjectStore(IObjectStore inner, ByteRateLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
    }

    /// <summary><paramref name="store"/> paced through <paramref name="limiter"/>, or itself when there is none.</summary>
    public static IObjectStore Over(IObjectStore store, ByteRateLimiter? limiter)
    {
        ThrowHelper.ThrowIfNull(store);
        return limiter is null ? store : new PacedObjectStore(store, limiter);
    }

    /// <inheritdoc />
    public StoreCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        _inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        var result = await _inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        return result is { Outcome: OpenReadOutcome.Found, Content: { } content }
            ? new OpenReadResult(new PacedStream(content, _limiter))
            : result;
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(openContent);
        return _inner.PutAsync(
            key,
            async token => new PacedStream(await openContent(token).ConfigureAwait(false), _limiter),
            conditions,
            cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        _inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(key, conditions, cancellationToken);
}
