using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// What <c>BlobMerkle</c> promises at the edges the twelve construction cases
/// in <c>Packing/BlobMerkleTests</c> do not reach — pinned against the
/// hand-rolled implementation before the construction moved onto a library,
/// so the move is held to them as well as to the arithmetic.
///
/// The verifier sits directly behind a peer's answer (ReplicaVerifier), so a
/// malformed answer must be refused and never thrown on: an exception where a
/// false belongs turns a dishonest destination into a failed pass instead of a
/// failed proof. The accumulator's hand-over is what the possession responder
/// builds its path from, and both of its finishing members reset it. And the
/// type is static, serving every writer, responder and verifier in a process at
/// once, which is safe only while the tree beneath it keeps no hash state
/// between calls.
///
/// Establishes FR-VER-001's refusal of a malformed possession answer. Does not
/// establish FR-VER-003: nothing here is about what a destination's status says.
/// </summary>
[TestClass]
public sealed class BlobMerkleBoundaryTests
{
    private static byte[] Pattern(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8) ^ seed);
        }

        return bytes;
    }

    private static ReadOnlyMemory<byte>[] Steps(byte[][] path) =>
        [.. path.Select(step => (ReadOnlyMemory<byte>)step)];

    [TestMethod]
    public void VerifyLeaf_AMalformedAnswer_IsRefusedRatherThanThrownOn()
    {
        var bytes = Pattern((2 * BlobMerkle.LeafSize) + 9);
        var root = BlobMerkle.Root(bytes);
        var path = Steps(BlobMerkle.AuthenticationPath(bytes, 1));
        var leaf = bytes.AsSpan(BlobMerkle.LeafSize, BlobMerkle.LeafSize).ToArray();

        Assert.IsTrue(
            BlobMerkle.VerifyLeaf(root, bytes.Length, 1, leaf, path),
            "the well-formed answer every refusal below perturbs");

        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, 0, 1, leaf, path), "a zero length");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, -1, 1, leaf, path), "a negative length");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, -1, leaf, path), "a negative index");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root.AsSpan(1), bytes.Length, 1, leaf, path), "a short root");
        Assert.IsFalse(BlobMerkle.VerifyLeaf([.. root, 0], bytes.Length, 1, leaf, path), "a long root");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, 1, leaf.AsSpan(1), path), "a truncated leaf");
        Assert.IsFalse(BlobMerkle.VerifyLeaf(root, bytes.Length, 1, [], path), "an empty leaf");
        Assert.IsFalse(
            BlobMerkle.VerifyLeaf(root, bytes.Length, 1, leaf, [path[0][..31], .. path[1..]]),
            "a step one byte short");
        Assert.IsFalse(
            BlobMerkle.VerifyLeaf(
                root, bytes.Length, 1, leaf, [.. Enumerable.Repeat(path[0], BlobMerkle.MaximumPathLength + 1)]),
            "a path past the bound");
    }

    [TestMethod]
    public void Accumulator_CompleteAndReset_HandsOverTheLeavesAPathIsBuiltFrom()
    {
        // The possession responder streams a blob past the accumulator and
        // answers from what it hands over, so a path built from those leaves
        // must verify against the root a writer published over the same bytes.
        var bytes = Pattern((3 * BlobMerkle.LeafSize) + 77);
        using var accumulator = new BlobMerkleAccumulator();
        accumulator.Append(bytes.AsSpan(0, 100_003));
        accumulator.Append(bytes.AsSpan(100_003));

        var (leafHashes, length) = accumulator.CompleteAndReset();

        Assert.AreEqual(bytes.Length, length);
        CollectionAssert.AreEqual(
            BlobMerkle.LeafHashes(bytes).Select(Convert.ToHexString).ToArray(),
            leafHashes.Select(Convert.ToHexString).ToArray());
        Assert.IsTrue(BlobMerkle.VerifyLeaf(
            BlobMerkle.Root(bytes), length, 3, bytes.AsSpan(3 * BlobMerkle.LeafSize),
            Steps(BlobMerkle.AuthenticationPath(leafHashes, 3))));
    }

    [TestMethod]
    public void Accumulator_AfterEitherFinish_TakesTheNextPreimageFromNothing()
    {
        var first = Pattern(BlobMerkle.LeafSize + 5, seed: 1);
        var second = Pattern(4099, seed: 2);
        using var accumulator = new BlobMerkleAccumulator();

        accumulator.Append(first);
        CollectionAssert.AreEqual(BlobMerkle.Root(first), accumulator.GetRootAndReset());

        accumulator.Append(first);
        Assert.AreEqual(first.Length, accumulator.CompleteAndReset().PreimageLength);

        accumulator.Append(second);
        CollectionAssert.AreEqual(BlobMerkle.Root(second), accumulator.GetRootAndReset());

        CollectionAssert.AreEqual(
            BlobMerkle.Root([]), accumulator.GetRootAndReset(), "nothing appended is the empty preimage's root");
    }

    [TestMethod]
    public void Root_ComputedOnManyThreadsAtOnce_IsTheRootComputedAlone()
    {
        var inputs = Enumerable.Range(0, 12).Select(i => Pattern((i * 197_137) + 1, seed: i)).ToArray();
        var expected = inputs.Select(input => Convert.ToHexString(BlobMerkle.Root(input))).ToArray();

        var actual = new string[inputs.Length * 6];
        Parallel.For(
            0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => actual[i] = Convert.ToHexString(BlobMerkle.Root(inputs[i % inputs.Length])));

        for (var i = 0; i < actual.Length; i++)
        {
            Assert.AreEqual(expected[i % inputs.Length], actual[i], $"root {i} computed concurrently");
        }
    }
}
