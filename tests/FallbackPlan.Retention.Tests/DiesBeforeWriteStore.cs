using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// A store whose process dies in front of one write: the <c>cut</c>-th put or
/// delete it is asked for, counted from one, never happens, and the token the
/// caller runs under is cancelled there, the way a dying process stops a
/// pass. Every write after it is refused too, because nothing runs after a
/// death.
/// </summary>
/// <remarks>
/// Reads pass through untouched. A write is the only durable effect a
/// collection pass has on a store, so a cut in front of each write in turn,
/// and one after the last, leaves the store in every state a collector that
/// dies can leave it in. <see cref="OperationCanceledException"/> is what a
/// death looks like from inside the pass: the collector reads every other
/// fault as a refusal to report and carry on from (ledger O1).
/// </remarks>
internal sealed class DiesBeforeWriteStore(IObjectStore inner, int cut, CancellationTokenSource process) : IObjectStore
{
    private readonly List<string> _asked = [];
    private int _writes;

    /// <summary>The writes asked for so far, the refused one included.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>Each write asked for, in order, as its verb and key.</summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>Whether the cut was reached.</summary>
    public bool Died => Writes >= cut;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        Write($"put {key}");
        return inner.PutAsync(key, openContent, conditions, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken)
    {
        Write($"delete {key}");
        return inner.DeleteAsync(key, conditions, cancellationToken);
    }

    private void Write(string asked)
    {
        lock (_asked)
        {
            _asked.Add(asked);
        }

        if (Interlocked.Increment(ref _writes) >= cut)
        {
            process.Cancel();
            throw new OperationCanceledException(process.Token);
        }
    }
}
