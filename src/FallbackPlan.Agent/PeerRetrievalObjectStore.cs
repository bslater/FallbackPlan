using System.Runtime.CompilerServices;
using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// A peer replica as an <see cref="IObjectStore"/> (peer-protocol 07,
/// ADR-0041): reads and listings travel the retrieval session; writes and
/// deletes do not exist — the replica is the destination's to hold and the
/// owner's to read, and a store that could write through this path would be
/// replication wearing the wrong hat. Ranged reads larger than one chunk are
/// fetched chunk by chunk; every consumer downstream — the repository open,
/// the catalogue rebuild, the blob reader — goes through this one interface,
/// which is the whole point.
/// </summary>
internal sealed class PeerRetrievalObjectStore(PeerRetrievalClient client) : IObjectStore
{
    /// <inheritdoc/>
    public StoreCapabilities Capabilities { get; } = new() { RangedReads = true };

    /// <inheritdoc/>
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var answer = await client.ReadAsync(key.Value, 0, 0, cancellationToken).ConfigureAwait(false);
        return answer.Found
            ? new GetMetadataResult(new ObjectMetadata((long)answer.TotalLength, LastModified: null))
            : GetMetadataResult.NotFound;
    }

    /// <inheritdoc/>
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        var buffered = new MemoryStream();
        var offset = (ulong)(range?.Offset ?? 0);
        ulong? want = range is { } bounded ? (ulong)bounded.Length : null;
        ulong total;

        var first = await client.ReadAsync(
            key.Value, offset, Math.Min(want ?? RetrieveRead.MaximumLength, RetrieveRead.MaximumLength),
            cancellationToken).ConfigureAwait(false);
        if (!first.Found)
        {
            return OpenReadResult.NotFound;
        }

        total = first.TotalLength;
        if (range is { } asked && (ulong)asked.Offset >= Math.Max(total, 1) && total > 0)
        {
            return OpenReadResult.RangeNotSatisfiable;
        }

        buffered.Write(first.Bytes.Span);
        var end = want is { } length ? Math.Min(offset + length, total) : total;
        var position = offset + (ulong)first.Bytes.Length;
        while (position < end)
        {
            var chunk = await client.ReadAsync(
                key.Value, position, Math.Min(end - position, RetrieveRead.MaximumLength), cancellationToken)
                .ConfigureAwait(false);
            if (!chunk.Found || chunk.Bytes.Length == 0)
            {
                break;
            }

            buffered.Write(chunk.Bytes.Span);
            position += (ulong)chunk.Bytes.Length;
        }

        buffered.Position = 0;
        return new OpenReadResult(buffered);
    }

    /// <inheritdoc/>
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("A peer replica is read-only over the retrieval session (07 §1).");

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);

        var after = options.ResumeAfter ?? string.Empty;
        while (true)
        {
            var page = await client.ListPageAsync(prefix.Value, after, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < page.Keys.Count; index++)
            {
                var key = ObjectKey.Parse(page.Keys[index]);
                yield return new ObjectEntry(key, (long)page.Lengths[index], page.Keys[index]);
            }

            if (!page.More || page.Keys.Count == 0)
            {
                yield break;
            }

            after = page.Keys[^1];
        }
    }

    /// <inheritdoc/>
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A peer replica is read-only over the retrieval session (07 §1).");
}
