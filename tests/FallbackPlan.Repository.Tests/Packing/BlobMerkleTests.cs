using System.Security.Cryptography;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The Merkle commitment over a sealed blob's bytes
/// ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md) open
/// question 4; specification 05 §5): an RFC 6962 tree over one-mebibyte
/// leaves of the same preimage the flat digest names, so a party holding only
/// the signed root can check a single leaf against it. Establishes
/// FR-VER-001's commitment half and NFR-COMP-004 for the pinned trees; the
/// challenge that carries a leaf over the wire is
/// <c>PeerReadBackVerificationTests</c>'.
/// </summary>
/// <remarks>
/// Does not establish FR-VER-003: this file proves the arithmetic, not what a
/// destination's status says about it.
/// </remarks>
[TestClass]
public sealed class BlobMerkleTests
{
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return bytes;
    }

    private static byte[] Leaf(ReadOnlySpan<byte> chunk)
    {
        var buffer = new byte[chunk.Length + 1];
        buffer[0] = 0x00;
        chunk.CopyTo(buffer.AsSpan(1));
        return SHA256.HashData(buffer);
    }

    private static byte[] Node(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var buffer = new byte[65];
        buffer[0] = 0x01;
        left.CopyTo(buffer.AsSpan(1));
        right.CopyTo(buffer.AsSpan(33));
        return SHA256.HashData(buffer);
    }

    /// <summary>The published root: the tree's head bound to the preimage's length.</summary>
    private static byte[] Bind(long length, ReadOnlySpan<byte> head)
    {
        var buffer = new byte[41];
        buffer[0] = 0x02;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1), (ulong)length);
        head.CopyTo(buffer.AsSpan(9));
        return SHA256.HashData(buffer);
    }

    [TestMethod]
    public void BlobMerkle_ALeafIsDomainSeparated_SoAOneLeafRootIsNotTheChunksBareDigest()
    {
        // RFC 6962's whole point: without the 0x00 prefix a one-leaf tree's
        // root would equal SHA-256 of the chunk, and a node's preimage could
        // be confused with a leaf's.
        var chunk = Pattern(1024);

        var root = BlobMerkle.Root(chunk);

        CollectionAssert.AreEqual(Bind(chunk.Length, Leaf(chunk)), root);
        Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(chunk)), Convert.ToHexString(root));
        Assert.AreNotEqual(Convert.ToHexString(Leaf(chunk)), Convert.ToHexString(root));
    }

    [TestMethod]
    public void BlobMerkle_ThreeLeaves_SplitAtTheLargestPowerOfTwoBelowTheCount()
    {
        // The split that separates RFC 6962 from a naive halving: k is the
        // largest power of two strictly below n, so three leaves are 2 + 1
        // and never 1 + 2. A tree built the other way has the same leaves
        // and a different root, which is why this is pinned rather than
        // assumed.
        var bytes = Pattern((2 * BlobMerkle.LeafSize) + 7);
        var a = Leaf(bytes.AsSpan(0, BlobMerkle.LeafSize));
        var b = Leaf(bytes.AsSpan(BlobMerkle.LeafSize, BlobMerkle.LeafSize));
        var c = Leaf(bytes.AsSpan(2 * BlobMerkle.LeafSize));

        var expected = Bind(bytes.Length, Node(Node(a, b), c));
        var naive = Bind(bytes.Length, Node(a, Node(b, c)));

        Assert.AreEqual(3, BlobMerkle.LeafCount(bytes.Length));
        CollectionAssert.AreEqual(expected, BlobMerkle.Root(bytes));
        Assert.AreNotEqual(Convert.ToHexString(naive), Convert.ToHexString(BlobMerkle.Root(bytes)));
    }

    [TestMethod]
    public void BlobMerkle_ALastLeafShorterThanTheLeafSize_IsStillOneLeaf()
    {
        var bytes = Pattern(BlobMerkle.LeafSize + 1);

        Assert.AreEqual(2, BlobMerkle.LeafCount(bytes.Length));
        CollectionAssert.AreEqual(
            Bind(
                bytes.Length,
                Node(Leaf(bytes.AsSpan(0, BlobMerkle.LeafSize)), Leaf(bytes.AsSpan(BlobMerkle.LeafSize)))),
            BlobMerkle.Root(bytes));
    }

    [TestMethod]
    public void BlobMerkle_TheAccumulatorFedInArbitrarySlices_AgreesWithTheWholeBufferRoot()
    {
        // The writer feeds the accumulator envelope, record and footer at a
        // time, never in leaf-sized pieces, so the split must be the
        // accumulator's business and not the caller's.
        var bytes = Pattern((3 * BlobMerkle.LeafSize) + 4097);
        var expected = BlobMerkle.Root(bytes);

        using var accumulator = new BlobMerkleAccumulator();
        var offset = 0;
        var slice = 1;
        while (offset < bytes.Length)
        {
            var take = Math.Min(slice, bytes.Length - offset);
            accumulator.Append(bytes.AsSpan(offset, take));
            offset += take;
            slice = (slice * 7) + 13;
        }

        CollectionAssert.AreEqual(expected, accumulator.GetRootAndReset());
    }

    [TestMethod]
    public void BlobMerkle_EveryLeafsPath_VerifiesAgainstTheRoot()
    {
        var bytes = Pattern((5 * BlobMerkle.LeafSize) + 11);
        var root = BlobMerkle.Root(bytes);
        var count = BlobMerkle.LeafCount(bytes.Length);
        Assert.AreEqual(6, count);

        for (var index = 0; index < count; index++)
        {
            var path = BlobMerkle.AuthenticationPath(bytes, index);
            var leaf = bytes.AsSpan(
                index * BlobMerkle.LeafSize,
                Math.Min(BlobMerkle.LeafSize, bytes.Length - (index * BlobMerkle.LeafSize)));

            Assert.IsTrue(
                BlobMerkle.VerifyLeaf(
                    root, bytes.Length, index, leaf, [.. path.Select(step => (ReadOnlyMemory<byte>)step)]),
                $"leaf {index} must verify");
        }
    }

    [TestMethod]
    public void BlobMerkle_APathOverTheWrongBytes_DoesNotVerify()
    {
        // The property the challenge rests on: the path alone proves nothing
        // — a destination that kept its leaf hashes and discarded the bytes
        // can still produce the path, and still cannot answer.
        var bytes = Pattern((2 * BlobMerkle.LeafSize) + 9);
        var root = BlobMerkle.Root(bytes);
        var path = BlobMerkle.AuthenticationPath(bytes, 1).Select(step => (ReadOnlyMemory<byte>)step).ToList();

        var tampered = bytes.AsSpan(BlobMerkle.LeafSize, BlobMerkle.LeafSize).ToArray();
        tampered[500] ^= 0x01;

        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, 1, tampered, path));
    }

    [TestMethod]
    public void BlobMerkle_ATamperedPathStep_DoesNotVerify()
    {
        var bytes = Pattern((3 * BlobMerkle.LeafSize) + 5);
        var root = BlobMerkle.Root(bytes);
        var path = BlobMerkle.AuthenticationPath(bytes, 2).Select(step => step.ToArray()).ToList();
        path[0][0] ^= 0x80;

        Assert.IsFalse(
            BlobMerkle.VerifyLeaf(
                root, bytes.Length, 2, bytes.AsSpan(2 * BlobMerkle.LeafSize, BlobMerkle.LeafSize),
                [.. path.Select(step => (ReadOnlyMemory<byte>)step)]));
    }

    [TestMethod]
    public void BlobMerkle_APathOfTheWrongLength_DoesNotVerify()
    {
        var bytes = Pattern((3 * BlobMerkle.LeafSize) + 5);
        var root = BlobMerkle.Root(bytes);
        var path = BlobMerkle.AuthenticationPath(bytes, 0).Select(step => (ReadOnlyMemory<byte>)step).ToList();
        var leaf = bytes.AsSpan(0, BlobMerkle.LeafSize);

        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, 0, leaf, path[..1]));
        Assert.IsFalse(
            BlobMerkle.VerifyLeaf(root, bytes.Length, 0, leaf, [.. path, (ReadOnlyMemory<byte>)new byte[32]]));
    }

    [TestMethod]
    public void BlobMerkle_ALeafIndexAtOrBeyondTheTree_DoesNotVerify()
    {
        var bytes = Pattern((2 * BlobMerkle.LeafSize) + 1);
        var root = BlobMerkle.Root(bytes);

        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, 3, bytes.AsSpan(0, 1), []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BlobMerkle.AuthenticationPath(bytes, 3));
    }

    [TestMethod]
    public void BlobMerkle_ALengthThatDisagreesWithTheSignedRoot_DoesNotVerify()
    {
        // The destination's declared length is what sizes the tree at the
        // source, so a destination that understates it would exempt its last
        // leaves from ever being drawn. Plain RFC 6962 permits that: a
        // four-leaf tree's first path has the length a three-leaf tree wants
        // and walks to the same head, so the size check alone accepts. The
        // length is bound into the root for exactly this case.
        var bytes = Pattern(4 * BlobMerkle.LeafSize);
        var root = BlobMerkle.Root(bytes);
        var path = BlobMerkle.AuthenticationPath(bytes, 0).Select(step => (ReadOnlyMemory<byte>)step).ToList();
        var leaf = bytes.AsSpan(0, BlobMerkle.LeafSize);

        Assert.IsTrue(BlobMerkle.VerifyLeaf(root, bytes.Length, 0, leaf, path));
        Assert.IsFalse(
            BlobMerkle.VerifyLeaf(root, 3 * BlobMerkle.LeafSize, 0, leaf, path),
            "understating the length by a whole leaf must not verify");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length - 1, 0, leaf, path));
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length + 1, 0, leaf, path));
    }

    [TestMethod]
    public void BlobMerkle_TheLeafSize_IsOneMebibyte()
    {
        // The format states the size rather than recording it per delta, so
        // both ends of a challenge agree by construction (05 §5).
        Assert.AreEqual(1024 * 1024, BlobMerkle.LeafSize);
        Assert.AreEqual(1, BlobMerkle.LeafCount(1));
        Assert.AreEqual(1, BlobMerkle.LeafCount(BlobMerkle.LeafSize));
        Assert.AreEqual(2, BlobMerkle.LeafCount(BlobMerkle.LeafSize + 1));
        Assert.AreEqual(512, BlobMerkle.LeafCount(FallbackPlan.Domain.FormatLimits.MaxBlobSize));
    }
}
