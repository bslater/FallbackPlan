using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// A stream whose reads and writes are paced through a <see cref="ByteRateLimiter"/>
/// (ADR-0074): what passes is charged, in chunks, and nothing else changes —
/// not a byte, not a position, not a length.
/// </summary>
/// <remarks>
/// <para>
/// One call moves at most <see cref="ChunkBytes"/>, so a whole-blob read pays
/// as it streams rather than running up one debt that stalls everything else
/// sharing the limiter behind it. A read is charged for what it returned; a
/// write is charged before each chunk goes out.
/// </para>
/// <para>
/// A synchronous call waits synchronously. Background work reads through the
/// pipeline's own threads, and blocking one of them for its share of a second
/// is the point; nothing paced here runs on a thread a person is waiting on,
/// because a person's work is never given a paced stream.
/// </para>
/// </remarks>
public sealed class PacedStream : Stream
{
    /// <summary>The most one read or write moves before it is charged.</summary>
    public const int ChunkBytes = 64 * 1024;

    private readonly Stream _inner;
    private readonly ByteRateLimiter _limiter;
    private readonly bool _leaveOpen;
    private bool _innerDisposed;

    /// <summary>Paces <paramref name="inner"/> through <paramref name="limiter"/>.</summary>
    /// <param name="inner">The stream the bytes really move through.</param>
    /// <param name="limiter">The limit they are charged to.</param>
    /// <param name="leaveOpen">Leave <paramref name="inner"/> open on dispose — a live session's stream outlives one exchange.</param>
    public PacedStream(Stream inner, ByteRateLimiter limiter, bool leaveOpen = false)
    {
        ThrowHelper.ThrowIfNull(inner);
        ThrowHelper.ThrowIfNull(limiter);
        _inner = inner;
        _limiter = limiter;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer[..Math.Min(buffer.Length, ChunkBytes)]);
        _limiter.AcquireAsync(read, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, ChunkBytes)], cancellationToken)
            .ConfigureAwait(false);
        await _limiter.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, ChunkBytes)];
            _limiter.AcquireAsync(chunk.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _inner.Write(chunk);
            buffer = buffer[chunk.Length..];
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, ChunkBytes)];
            await _limiter.AcquireAsync(chunk.Length, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            buffer = buffer[chunk.Length..];
        }
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen && !_innerDisposed)
        {
            _innerDisposed = true;
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen && !_innerDisposed)
        {
            _innerDisposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
