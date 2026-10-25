using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Runs another actor's work once, when the caller first opens one key to read
/// it, and only then serves the read: so a test can place that work after a
/// copy has listed the key and before it reads it.
/// </summary>
/// <remarks>
/// <see cref="InterleavingObjectStore"/>'s twin for a reader that reads while
/// it lists, where the end of a listing comes too late.
/// </remarks>
/// <param name="inner">The store doing the actual work.</param>
/// <param name="trigger">The key whose first read runs the work.</param>
/// <param name="interleave">The other actor's work.</param>
public sealed class InterleavingOnReadObjectStore(IObjectStore inner, ObjectKey trigger, Func<Task> interleave) : IObjectStore
{
    private int _fired;

    /// <summary>Whether the interleaved work has run.</summary>
    public bool Fired => Volatile.Read(ref _fired) == 1;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        if (key == trigger && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            await interleave().ConfigureAwait(false);
        }

        return await inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}
