namespace FallbackPlan.TestSupport;

/// <summary>
/// A deterministic pseudorandom stream of arbitrary length that holds only
/// one small block: input that can be archived without ever allocating it,
/// which is what lets a bounded-memory claim be measured at a size no test
/// machine could hold.
/// </summary>
public sealed class SyntheticStream(long length, int seed) : Stream
{
    private readonly byte[] _block = CreateBlock(seed);
    private long _position;

    private static byte[] CreateBlock(int seed)
    {
        // One 1 MiB pseudorandom block, repeated with a rolling XOR so the
        // content neither compresses to nothing nor repeats exactly (exact
        // repetition would make every cdc segment identical).
        var block = new byte[1024 * 1024];
        new Random(seed).NextBytes(block);
        return block;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = (int)Math.Min(count, length - _position);
        for (var index = 0; index < take; index++)
        {
            var absolute = _position + index;
            var salt = (byte)((absolute >> 20) * 31);
            buffer[offset + index] = (byte)(_block[absolute % _block.Length] ^ salt);
        }

        _position += take;
        return take;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
