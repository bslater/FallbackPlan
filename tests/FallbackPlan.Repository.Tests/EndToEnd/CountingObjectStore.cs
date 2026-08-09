using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A pass-through store that counts reads, so a test can assert on what a
/// publication actually fetched rather than on what it should have.
/// </summary>
/// <remarks>
/// Verify-on-reuse is a decision about reads, so "how many" is the only
/// honest oracle for it. Counting <see cref="OpenReadAsync"/> counts range
/// reads, which is the unit the store bills in and the unit
/// <see cref="Packing.BlobReader"/> is written to minimise.
/// </remarks>
internal sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
{
    private int _reads;

    /// <summary>Range reads issued since construction.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return inner.OpenReadAsync(key, range, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}
