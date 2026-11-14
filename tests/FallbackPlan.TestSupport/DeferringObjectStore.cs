using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Defers each put of a matching key until a condition the test names has
/// come about, or has been waited on as long as the test allows: so a test
/// can hold one step of a writer back until another actor has been seen to
/// act, without the writer ever knowing.
/// </summary>
/// <remarks>
/// The condition decides only when the put goes ahead, never whether: a test
/// that needs to know whether the other actor acted in time asks its own
/// condition, which it can record.
/// </remarks>
/// <param name="inner">The store doing the actual work.</param>
/// <param name="keyFilter">Which keys' puts wait.</param>
/// <param name="until">What a matching put waits on before it goes ahead.</param>
public sealed class DeferringObjectStore(IObjectStore inner, Func<string, bool> keyFilter, Func<Task> until) : IObjectStore
{
    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        if (keyFilter(key.ToString()))
        {
            await until().ConfigureAwait(false);
        }

        return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}
