using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.TestSupport;

/// <summary>
/// A store that consumes and discards every put: the benchmarks and the
/// bounded-memory proof measure the pipeline — segmentation, hashing,
/// compression, encryption, packing — not the disk behind it, and neither
/// can afford to retain what it wrote. Reads and listings answer
/// "nothing here".
/// </summary>
public sealed class DiscardingObjectStore : IObjectStore
{
    private long _bytesConsumed;
    private int _puts;

    // Uploads run concurrently now (ADR-0029 §2), so the benchmark's own store
    // is called from several workers at once. Plain increments here would be a
    // data race in the measuring instrument.
    /// <summary>Total bytes consumed across all puts.</summary>
    public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);

    /// <summary>Number of puts accepted.</summary>
    public int Puts => Volatile.Read(ref _puts);

    /// <inheritdoc />
    public StoreCapabilities Capabilities { get; } = new()
    {
        ConditionalCreate = true,
        RangedReads = true,
        ListingConsistency = ListingConsistency.Strong,
        MaximumObjectSize = long.MaxValue,
    };

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetMetadataResult.NotFound);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        ValueTask.FromResult(OpenReadResult.NotFound);

    /// <inheritdoc />
    public async ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        var content = await openContent(cancellationToken);
        await using (content)
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                Interlocked.Add(ref _bytesConsumed, read);
            }
        }

        Interlocked.Increment(ref _puts);
        return new PutResult(PutOutcome.Created);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DeleteResult(DeleteOutcome.NotFound));
}
