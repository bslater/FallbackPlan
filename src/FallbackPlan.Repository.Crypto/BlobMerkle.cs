using System.Security.Cryptography;
using Bodu.Security.Cryptography;

namespace FallbackPlan.Repository.Crypto;

/// <summary>
/// The Merkle commitment over a sealed blob's bytes (specification 05 §5;
/// [ADR-0065](../../docs/adr/0065-merkle-commitment-and-chunk-possession.md),
/// from [ADR-0052](../../docs/adr/0052-relocatable-records-format-v3.md) open
/// question 4). An RFC 6962 tree over one-mebibyte leaves of exactly the
/// preimage the flat digest names — <c>[0, length − 16)</c>, everything
/// before the locator — so the two commitments describe the same bytes and a
/// reader that holds only the signed root can check a single leaf against it
/// without the blob.
/// </summary>
/// <remarks>
/// The tree is Bodu's <see cref="MerkleTree"/> over SHA-256 at RFC 6962's
/// fan-out of two: leaf <c>H(0x00 ‖ chunk)</c>, node <c>H(0x01 ‖ l ‖ r)</c>,
/// split at the largest power of two below the count, and the published root
/// <c>H(0x02 ‖ u64_be(length) ‖ head)</c>. This type owns the format's binding
/// of that tree — the leaf size, the preimage, the bound root and the bound on
/// a peer's path — and none of its arithmetic. It lives here rather than
/// beside the blob it commits to because only this project may reference
/// <c>Bodu.Security.Cryptography</c> (ADR-0019).
/// <para>
/// The leaf size is stated by the format rather than recorded per blob, so
/// both ends of a challenge agree by construction; a size a destination could
/// be told is a size a destination could make cheap.
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

    /// <summary>
    /// The format's tree. It holds only its configuration and draws a fresh
    /// algorithm from the factory for every computation, which is what lets
    /// one instance serve every writer, responder and verifier at once.
    /// </summary>
    private static readonly MerkleTree Tree = new(SHA256.Create);

    /// <summary>The number of leaves a preimage of this length has.</summary>
    public static int LeafCount(long preimageLength) =>
        (int)MerkleTree.BlockCount(preimageLength, LeafSize);

    /// <summary>The offset at which a leaf's chunk begins.</summary>
    public static long LeafOffset(int leafIndex) => MerkleTree.BlockOffset(leafIndex, LeafSize);

    /// <summary>The length of the chunk a leaf covers, which is short only for the last.</summary>
    public static int LeafLength(long preimageLength, int leafIndex) =>
        MerkleTree.BlockLength(preimageLength, leafIndex, LeafSize);

    /// <summary>The Merkle root over a whole preimage held in memory.</summary>
    public static byte[] Root(ReadOnlySpan<byte> preimage) =>
        Tree.BindRoot(Tree.ComputeRootOfBlocks(preimage, LeafSize), preimage.Length);

    /// <summary>One hash per leaf, in order.</summary>
    public static byte[][] LeafHashes(ReadOnlySpan<byte> preimage) =>
        [.. Tree.ComputeBlocked(preimage, LeafSize).LeafHashes];

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
    public static byte[] RootOfLeafHashes(IReadOnlyList<byte[]> leafHashes, long preimageLength) =>
        Tree.BindRoot(Tree.ComputeRootOfLeafHashes(leafHashes), preimageLength);

    /// <summary>The leaf hash of one chunk: <c>SHA-256(0x00 ‖ chunk)</c>.</summary>
    public static byte[] HashLeaf(ReadOnlySpan<byte> chunk) => Tree.HashLeaf(chunk);

    /// <summary>
    /// The authentication path for one leaf of a preimage held in memory:
    /// the sibling subtree roots from the leaf upward, as RFC 6962 §2.1.1
    /// consumes them.
    /// </summary>
    public static byte[][] AuthenticationPath(ReadOnlySpan<byte> preimage, int leafIndex) =>
        AuthenticationPath(Tree.ComputeBlocked(preimage, LeafSize).LeafHashes, leafIndex);

    /// <summary>
    /// The authentication path for one leaf, from hashes already computed —
    /// what a party that streamed a blob past itself can answer without
    /// holding the blob in memory.
    /// </summary>
    public static byte[][] AuthenticationPath(IReadOnlyList<byte[]> leafHashes, int leafIndex) =>
        Tree.AuthenticationPath(leafHashes, leafIndex);

    /// <summary>
    /// Checks one leaf's bytes against a signed root (RFC 6962 §2.1.1). The
    /// leaf's bytes are the proof: a party that kept the path and discarded
    /// the chunk can still produce the path and still cannot answer, which
    /// is the whole difference between this and a digest it could have
    /// cached at receipt.
    /// </summary>
    /// <remarks>
    /// The tree's size is derived from the bound length rather than taken
    /// from anyone, and the leaf must be exactly as long as its position
    /// requires. The path and the leaf arrive from the party being examined,
    /// so every malformed answer is refused rather than thrown on.
    /// </remarks>
    public static bool VerifyLeaf(
        ReadOnlySpan<byte> root,
        long preimageLength,
        int leafIndex,
        ReadOnlySpan<byte> leaf,
        IReadOnlyList<ReadOnlyMemory<byte>> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Count <= MaximumPathLength
            && Tree.VerifyBlockInclusion(root, preimageLength, LeafSize, leafIndex, leaf, path);
    }
}

/// <summary>
/// Builds <see cref="BlobMerkle"/>'s root from bytes arriving in whatever
/// pieces the writer produces them in. It rides beside the blob's flat
/// digest and is fed by exactly the same calls, so the commitment costs no
/// second pass over the sealed bytes.
/// </summary>
/// <remarks>
/// The leaves are hashed here, streamed, and everything above them is the
/// library's. Its own accumulator hashes a leaf in one call and so holds a
/// whole one — a fresh mebibyte for every blob written, every spool resumed
/// and every challenge answered — where streaming each leaf into its hash
/// holds none, which is the bound SpoolCheckpointTests keeps a resume to.
/// The leaf hash is RFC 6962's <c>H(0x00 ‖ chunk)</c>, the same one
/// <see cref="BlobMerkle.HashLeaf"/> computes, and the conformance vectors
/// hold the two to each other.
/// </remarks>
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
        var (leaves, length) = CompleteAndReset();
        return BlobMerkle.RootOfLeafHashes(leaves, length);
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
