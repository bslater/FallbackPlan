using System.Security.Cryptography;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// The Merkle commitment over a sealed blob's bytes (specification 05 §5;
/// [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md) open
/// question 4). An RFC 6962 tree over one-mebibyte leaves of exactly the
/// preimage the flat digest names — <c>[0, length − 16)</c>, everything
/// before the locator — so the two commitments describe the same bytes and a
/// reader that holds only the signed root can check a single leaf against it
/// without the blob.
/// </summary>
/// <remarks>
/// The leaf and node prefixes are RFC 6962's and are not decoration: without
/// them a one-leaf tree's root would be the chunk's bare digest and an
/// interior node's preimage could be mistaken for a leaf's, which is how a
/// second tree is made to produce a root somebody already signed.
/// <para>
/// The leaf size is stated by the format rather than recorded per blob, so
/// both ends of a challenge agree by construction; a size a destination
/// could be told is a size a destination could make cheap. At the 512 MiB
/// blob ceiling a tree has at most 512 leaves, so the leaf hashes are kept
/// whole rather than folded through a logarithmic stack — 16 KiB, and the
/// arithmetic reads as the specification writes it.
/// </para>
/// </remarks>
public static class BlobMerkle
{
    /// <summary>The chunk one leaf covers: one mebibyte, fixed by the format.</summary>
    public const int LeafSize = 1024 * 1024;

    /// <summary>The root's width, and every path step's.</summary>
    public const int RootLength = 32;

    /// <summary>
    /// The most steps any path can carry. A blob is at most 512 leaves, so
    /// nine would do; the bound exists to stop a peer's answer allocating,
    /// and is stated with room rather than at the edge.
    /// </summary>
    public const int MaximumPathLength = 32;

    private const byte LeafPrefix = 0x00;
    private const byte NodePrefix = 0x01;
    private const byte RootPrefix = 0x02;

    /// <summary>The number of leaves a preimage of this length has.</summary>
    public static int LeafCount(long preimageLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preimageLength);

        return (int)((preimageLength + LeafSize - 1) / LeafSize);
    }

    /// <summary>The offset at which a leaf's chunk begins.</summary>
    public static long LeafOffset(int leafIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);

        return (long)leafIndex * LeafSize;
    }

    /// <summary>The length of the chunk a leaf covers, which is short only for the last.</summary>
    public static int LeafLength(long preimageLength, int leafIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preimageLength);
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);

        var offset = LeafOffset(leafIndex);
        if (offset >= preimageLength)
        {
            return 0;
        }

        return (int)Math.Min(LeafSize, preimageLength - offset);
    }

    /// <summary>The Merkle root over a whole preimage held in memory.</summary>
    public static byte[] Root(ReadOnlySpan<byte> preimage) =>
        RootOfLeafHashes(LeafHashes(preimage), preimage.Length);

    /// <summary>One hash per leaf, in order.</summary>
    public static byte[][] LeafHashes(ReadOnlySpan<byte> preimage)
    {
        var count = LeafCount(preimage.Length);
        var leaves = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = index * LeafSize;
            leaves[index] = HashLeaf(preimage.Slice(offset, Math.Min(LeafSize, preimage.Length - offset)));
        }

        return leaves;
    }

    /// <summary>
    /// RFC 6962's MTH over leaf hashes already computed, bound to the
    /// preimage's length.
    /// </summary>
    /// <remarks>
    /// The length binding is not ornament, and the plain MTH is not enough.
    /// RFC 6962's verifier is given the tree size by its caller, and for a
    /// four-leaf tree's first leaf the path a three-leaf tree wants has the
    /// same length: hand that path over under a claimed size of three and
    /// the walk lands on the four-leaf root and accepts. Here the size comes
    /// from what the destination says its copy is, so a destination could
    /// shorten its claim, exempt the last leaf from ever being drawn, and
    /// still answer every challenge. Hashing the length into the root under
    /// a prefix of its own makes the commitment name one tree and no other.
    /// </remarks>
    public static byte[] RootOfLeafHashes(IReadOnlyList<byte[]> leafHashes, long preimageLength)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        ArgumentOutOfRangeException.ThrowIfNegative(preimageLength);

        Span<byte> buffer = stackalloc byte[1 + sizeof(ulong) + RootLength];
        buffer[0] = RootPrefix;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer[1..], (ulong)preimageLength);
        Mth([.. leafHashes]).CopyTo(buffer[(1 + sizeof(ulong))..]);

        return SHA256.HashData(buffer);
    }

    /// <summary>The leaf hash of one chunk: <c>SHA-256(0x00 ‖ chunk)</c>.</summary>
    public static byte[] HashLeaf(ReadOnlySpan<byte> chunk)
    {
        var buffer = new byte[chunk.Length + 1];
        buffer[0] = LeafPrefix;
        chunk.CopyTo(buffer.AsSpan(1));

        var hash = SHA256.HashData(buffer);
        CryptographicOperations.ZeroMemory(buffer);
        return hash;
    }

    /// <summary>
    /// The authentication path for one leaf of a preimage held in memory:
    /// the sibling subtree roots from the leaf upward, as RFC 6962 §2.1.1
    /// consumes them.
    /// </summary>
    public static byte[][] AuthenticationPath(ReadOnlySpan<byte> preimage, int leafIndex) =>
        AuthenticationPath(LeafHashes(preimage), leafIndex);

    /// <summary>
    /// The authentication path for one leaf, from hashes already computed —
    /// what a party that streamed a blob past itself can answer without
    /// holding the blob in memory.
    /// </summary>
    public static byte[][] AuthenticationPath(IReadOnlyList<byte[]> leafHashes, int leafIndex)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndex, leafHashes.Count);

        var path = new List<byte[]>();
        AppendPath([.. leafHashes], leafIndex, path);
        return [.. path];
    }

    /// <summary>
    /// Checks one leaf's bytes against a signed root (RFC 6962 §2.1.1). The
    /// leaf's bytes are the proof: a party that kept the path and discarded
    /// the chunk can still produce the path and still cannot answer, which
    /// is the whole difference between this and a digest it could have
    /// cached at receipt.
    /// </summary>
    public static bool VerifyLeaf(
        ReadOnlySpan<byte> root,
        long preimageLength,
        int leafIndex,
        ReadOnlySpan<byte> leaf,
        IReadOnlyList<ReadOnlyMemory<byte>> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (root.Length != RootLength
            || preimageLength <= 0
            || leafIndex < 0
            || leafIndex >= LeafCount(preimageLength)
            || path.Count > MaximumPathLength
            || leaf.Length != LeafLength(preimageLength, leafIndex))
        {
            return false;
        }

        var treeSize = LeafCount(preimageLength);

        Span<byte> running = stackalloc byte[RootLength];
        HashLeaf(leaf).CopyTo(running);

        var fn = (uint)leafIndex;
        var sn = (uint)(treeSize - 1);

        foreach (var step in path)
        {
            if (sn == 0 || step.Length != RootLength)
            {
                return false;
            }

            if ((fn & 1) == 1 || fn == sn)
            {
                HashNode(step.Span, running, running);
                while ((fn & 1) == 0 && fn != 0)
                {
                    fn >>= 1;
                    sn >>= 1;
                }
            }
            else
            {
                HashNode(running, step.Span, running);
            }

            fn >>= 1;
            sn >>= 1;
        }

        if (sn != 0)
        {
            return false;
        }

        Span<byte> bound = stackalloc byte[1 + sizeof(ulong) + RootLength];
        bound[0] = RootPrefix;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bound[1..], (ulong)preimageLength);
        running.CopyTo(bound[(1 + sizeof(ulong))..]);

        Span<byte> actual = stackalloc byte[RootLength];
        SHA256.HashData(bound, actual);

        return CryptographicOperations.FixedTimeEquals(actual, root);
    }

    private static void AppendPath(ReadOnlySpan<byte[]> leaves, int index, List<byte[]> path)
    {
        if (leaves.Length <= 1)
        {
            return;
        }

        var split = SplitPoint(leaves.Length);
        if (index < split)
        {
            AppendPath(leaves[..split], index, path);
            path.Add(Mth(leaves[split..]));
        }
        else
        {
            AppendPath(leaves[split..], index - split, path);
            path.Add(Mth(leaves[..split]));
        }
    }

    private static byte[] Mth(ReadOnlySpan<byte[]> leaves)
    {
        // The empty tree is the empty string's digest, as RFC 6962 defines
        // it. No sealed blob is empty — every one carries an envelope — but
        // the function is total or the recursion has a hole in it.
        if (leaves.Length == 0)
        {
            return SHA256.HashData([]);
        }

        if (leaves.Length == 1)
        {
            return leaves[0];
        }

        var split = SplitPoint(leaves.Length);
        var node = new byte[RootLength];
        HashNode(Mth(leaves[..split]), Mth(leaves[split..]), node);
        return node;
    }

    /// <summary>
    /// The largest power of two strictly below <paramref name="count"/> —
    /// RFC 6962's split, which is not a halving. Three leaves are 2 + 1 and
    /// never 1 + 2, and a tree built the other way has the same leaves and a
    /// different root.
    /// </summary>
    private static int SplitPoint(int count)
    {
        var split = 1;
        while (split * 2 < count)
        {
            split *= 2;
        }

        return split;
    }

    private static void HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        Span<byte> buffer = stackalloc byte[1 + (2 * RootLength)];
        buffer[0] = NodePrefix;
        left.CopyTo(buffer[1..]);
        right.CopyTo(buffer[(1 + RootLength)..]);
        SHA256.HashData(buffer, destination);
    }
}

/// <summary>
/// Builds <see cref="BlobMerkle"/>'s root from bytes arriving in whatever
/// pieces the writer produces them in. It rides beside the blob's flat
/// digest and is fed by exactly the same calls, so the commitment costs no
/// second pass over the sealed bytes.
/// </summary>
public sealed class BlobMerkleAccumulator : IDisposable
{
    private readonly IncrementalHash _leaf = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<byte[]> _leaves = [];
    private int _inLeaf;
    private long _length;
    private bool _disposed;

    /// <summary>Adds bytes, splitting them across leaf boundaries as it goes.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (!bytes.IsEmpty)
        {
            if (_inLeaf == 0)
            {
                _leaf.AppendData([0x00]);
            }

            var take = Math.Min(BlobMerkle.LeafSize - _inLeaf, bytes.Length);
            _leaf.AppendData(bytes[..take]);
            _inLeaf += take;
            _length += take;
            bytes = bytes[take..];

            if (_inLeaf == BlobMerkle.LeafSize)
            {
                CloseLeaf();
            }
        }
    }

    /// <summary>
    /// Finishes the tree and starts an empty one. A partial trailing leaf is
    /// a leaf; an exact multiple of the leaf size adds no empty one.
    /// </summary>
    public byte[] GetRootAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inLeaf > 0)
        {
            CloseLeaf();
        }

        var root = BlobMerkle.RootOfLeafHashes(_leaves, _length);
        _leaves.Clear();
        _length = 0;
        return root;
    }

    /// <summary>
    /// Finishes the tree and hands over its leaf hashes, which is what a
    /// party building an authentication path needs and a party publishing a
    /// root does not. At the blob ceiling this is 512 hashes; the bytes they
    /// cover were streamed past and never held.
    /// </summary>
    public (IReadOnlyList<byte[]> LeafHashes, long PreimageLength) CompleteAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inLeaf > 0)
        {
            CloseLeaf();
        }

        var leaves = _leaves.ToArray();
        var length = _length;
        _leaves.Clear();
        _length = 0;
        return (leaves, length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _leaf.Dispose();
    }

    private void CloseLeaf()
    {
        _leaves.Add(_leaf.GetHashAndReset());
        _inLeaf = 0;
    }
}
