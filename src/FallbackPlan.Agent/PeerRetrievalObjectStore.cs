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
/// replication wearing the wrong hat. A read of any size is served as a
/// stream that fetches one chunk at a time as it is consumed, so a whole
/// blob copied back into a staging archive or restored to a person holds
/// one chunk in memory and never the object (NFR-PERF-001); every consumer
/// downstream — the repository open, the catalogue rebuild, the blob
/// reader, the heal — goes through this one interface, which is the whole
/// point.
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
        var offset = (ulong)(range?.Offset ?? 0);
        ulong? want = range is { } bounded ? (ulong)bounded.Length : null;

        // The first chunk is fetched eagerly: it answers whether the object
        // exists and how long it is, which the caller needs now. The rest is
        // fetched as the stream is read.
        var first = await client.ReadAsync(
            key.Value, offset, Math.Min(want ?? RetrieveRead.MaximumLength, RetrieveRead.MaximumLength),
            cancellationToken).ConfigureAwait(false);
        if (!first.Found)
        {
            return OpenReadResult.NotFound;
        }

        var total = first.TotalLength;
        if (range is { } asked && (ulong)asked.Offset >= Math.Max(total, 1) && total > 0)
        {
            return OpenReadResult.RangeNotSatisfiable;
        }

        var end = want is { } length ? Math.Min(offset + length, total) : total;
        return new OpenReadResult(new ChunkedReadStream(client, key.Value, first.Bytes, offset, end));
    }

    /// <summary>
    /// A forward-only read over one object, one chunk in memory at a time:
    /// the chunk already fetched, then each next one as the previous is
    /// consumed, up to the end of the range asked for.
    /// </summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly PeerRetrievalClient _client;
        private readonly string _key;
        private readonly ulong _start;
        private readonly ulong _end;
        private ReadOnlyMemory<byte> _chunk;
        private int _consumed;
        private ulong _fetched;
        private ulong _position;

        public ChunkedReadStream(PeerRetrievalClient client, string key, ReadOnlyMemory<byte> first, ulong start, ulong end)
        {
            _client = client;
            _key = key;
            _start = start;
            _end = end;
            _chunk = first;
            _fetched = start + (ulong)first.Length;
            _position = start;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => (long)(_end - _start);

        public override long Position
        {
            get => (long)(_position - _start);
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_consumed == _chunk.Length)
            {
                if (_fetched >= _end)
                {
                    return 0;
                }

                var next = await _client.ReadAsync(
                    _key, _fetched, Math.Min(_end - _fetched, RetrieveRead.MaximumLength), cancellationToken)
                    .ConfigureAwait(false);
                if (!next.Found || next.Bytes.Length == 0)
                {
                    // The object shrank or vanished under the read: end the
                    // stream short rather than spin, and let the consumer's
                    // own length or authentication check name it.
                    return 0;
                }

                _chunk = next.Bytes;
                _consumed = 0;
                _fetched += (ulong)next.Bytes.Length;
            }

            var count = Math.Min(buffer.Length, _chunk.Length - _consumed);
            _chunk.Span.Slice(_consumed, count).CopyTo(buffer.Span);
            _consumed += count;
            _position += (ulong)count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
