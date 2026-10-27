using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A pass-through store that counts reads and the bytes they returned, so a
/// test can assert on what a publication or a restore actually fetched
/// rather than on what it should have.
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
    private long _bytesRead;

    /// <summary>Range reads issued since construction.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>The bytes every read took from the store between them: what a metered store bills for.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        var opened = await inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        return opened.Outcome == OpenReadOutcome.Found
            ? new OpenReadResult(new CountingStream(opened.Content!, this))
            : opened;
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

    /// <summary>A read-only stream that adds what is read through it to its store's count.</summary>
    private sealed class CountingStream(Stream inner, CountingObjectStore owner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Counted(int read)
        {
            Interlocked.Add(ref owner._bytesRead, read);
            return read;
        }
    }
}
