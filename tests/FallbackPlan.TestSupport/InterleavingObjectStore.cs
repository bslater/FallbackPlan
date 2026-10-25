using System.Runtime.CompilerServices;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Runs another actor's work once, at the moment the first listing under a
/// prefix has yielded its last entry and before the caller learns the
/// listing is over, so a test can place that work exactly between two steps
/// of a reader: after it has seen one plane and before it looks at the next.
/// </summary>
/// <remarks>
/// The interleaved work runs against whatever store the test hands it, which
/// is normally the inner one: it is another actor's, so its own calls are not
/// the reader's and are not seen by anything wrapping this store.
/// </remarks>
/// <param name="inner">The store doing the actual work.</param>
/// <param name="listingPrefix">The prefix whose first listing, once exhausted, runs the work.</param>
/// <param name="interleave">The other actor's work.</param>
public sealed class InterleavingObjectStore(IObjectStore inner, string listingPrefix, Func<Task> interleave) : IObjectStore
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
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        inner.PutAsync(key, openContent, conditions, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in inner.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }

        if (string.Equals(prefix.Value, listingPrefix, StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _fired, 1) == 0)
        {
            await interleave().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}
