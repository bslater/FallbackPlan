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
    private long _keyedStretch = -1;
    private ulong _stretchKey;

    private static byte[] CreateBlock(int seed)
    {
        // One 1 MiB pseudorandom block, repeated with a rolling XOR so the
        // content neither compresses to nothing nor repeats exactly (exact
        // repetition would make every cdc segment identical).
        var block = new byte[1024 * 1024];
        new Random(seed).NextBytes(block);
        return block;
    }

    private static ulong KeyFor(long stretch)
    {
        if (stretch == 0)
        {
            return 0;
        }

        // SplitMix64's finaliser: a bijection, so no two stretches share a key.
        var key = (ulong)stretch * 0x9E3779B97F4A7C15UL;
        key = (key ^ (key >> 30)) * 0xBF58476D1CE4E5B9UL;
        key = (key ^ (key >> 27)) * 0x94D049BB133111EBUL;
        return key ^ (key >> 31);
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

            // The per-mebibyte salt is a byte, so it comes round again every
            // 256 MiB, and content that repeats is measured as deduplication:
            // a repeated segment is never compressed, encrypted, packed or
            // uploaded. Each 256 MiB stretch after the first is keyed apart
            // from the others; the first keeps the bytes the suites archiving
            // less than that were measured on.
            var stretch = absolute >> 28;
            if (stretch != _keyedStretch)
            {
                _keyedStretch = stretch;
                _stretchKey = KeyFor(stretch);
            }

            var salt = (byte)(((absolute >> 20) * 31) ^ (long)(_stretchKey >> (int)((absolute & 7) << 3)));
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
