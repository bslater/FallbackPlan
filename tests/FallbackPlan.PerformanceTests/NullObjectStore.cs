using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// A store that consumes and discards every put: the benchmarks measure the
/// pipeline — segmentation, hashing, compression, encryption, packing — not
/// the disk behind it (NFR-PERF-001's shape). Reads and listings answer
/// "nothing here".
/// </summary>
public sealed class NullObjectStore : IObjectStore
{
    /// <summary>Total bytes consumed across all puts.</summary>
    public long BytesConsumed { get; private set; }

    /// <summary>Number of puts accepted.</summary>
    public int Puts { get; private set; }

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
                BytesConsumed += read;
            }
        }

        Puts++;
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
