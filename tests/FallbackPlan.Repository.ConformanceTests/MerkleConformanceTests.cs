using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>merkle.json</c> through the real commitment (specification 05
/// §5; [ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
/// open question 4; NFR-COMP-004, NFR-SEC-003): the RFC 6962 prefixes, the
/// split that is not a halving, the length bound on the published root, and
/// every authentication path the vector states.
/// </summary>
/// <remarks>
/// Does not establish FR-VER-001: what a peer is asked and what a source
/// counts as proved is <c>PeerReadBackVerificationTests</c>'; this file
/// establishes only that both ends would compute the same tree.
/// </remarks>
[TestClass]
public sealed class MerkleConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "merkle.json")));

    /// <summary>The vector's stream: concatenated SHA-256(BE64(counter)), truncated.</summary>
    private static byte[] Stream(int length)
    {
        var bytes = new byte[length];
        var counter = 0UL;
        var written = 0;
        Span<byte> block = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> input = stackalloc byte[sizeof(ulong)];
        while (written < length)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(input, counter);
            SHA256.HashData(input, block);
            var take = Math.Min(block.Length, length - written);
            block[..take].CopyTo(bytes.AsSpan(written));
            written += take;
            counter++;
        }

        return bytes;
    }

    [TestMethod]
    public void Merkle_TheStatedParameters_AreTheBuildsOwn()
    {
        var parameters = Vectors.RootElement.GetProperty("parameters");
        Assert.AreEqual(BlobMerkle.LeafSize, parameters.GetProperty("leaf_size").GetInt32());
        Assert.AreEqual("00", parameters.GetProperty("leaf_prefix").GetString());
        Assert.AreEqual("01", parameters.GetProperty("node_prefix").GetString());
        Assert.AreEqual("02", parameters.GetProperty("root_prefix").GetString());
    }

    [TestMethod]
    public void Merkle_ALeafHash_IsDomainSeparatedFromThePlainDigest()
    {
        var primitives = Vectors.RootElement.GetProperty("primitives");

        Assert.AreEqual(
            primitives.GetProperty("leaf_of_abc").GetString(),
            Convert.ToHexStringLower(BlobMerkle.HashLeaf("abc"u8)));
        Assert.AreEqual(
            primitives.GetProperty("leaf_of_empty_chunk").GetString(),
            Convert.ToHexStringLower(BlobMerkle.HashLeaf([])));
        Assert.AreNotEqual(
            primitives.GetProperty("sha256_of_abc").GetString(),
            primitives.GetProperty("leaf_of_abc").GetString());
    }

    [TestMethod]
    public void Merkle_EveryPinnedShape_FoldsToTheStatedRoot()
    {
        var synthetic = Vectors.RootElement.GetProperty("synthetic_leaf_hashes")
            .EnumerateArray()
            .Select(value => Convert.FromHexString(value.GetString()!))
            .ToList();

        foreach (var shape in Vectors.RootElement.GetProperty("shapes").EnumerateArray())
        {
            var count = shape.GetProperty("leaf_count").GetInt32();
            var length = shape.GetProperty("preimage_length").GetInt64();

            Assert.AreEqual(count, BlobMerkle.LeafCount(length), $"leaf count for {length} bytes");
            Assert.AreEqual(
                shape.GetProperty("root").GetString(),
                Convert.ToHexStringLower(BlobMerkle.RootOfLeafHashes(synthetic[..count], length)),
                $"root for {count} leaves");
        }
    }

    [TestMethod]
    public void Merkle_EveryPinnedPath_VerifiesAndOnlyForItsOwnLeaf()
    {
        var pinned = Vectors.RootElement.GetProperty("authentication_paths");
        var length = pinned.GetProperty("preimage_length").GetInt32();
        var root = Convert.FromHexString(pinned.GetProperty("root").GetString()!);
        var count = pinned.GetProperty("leaf_count").GetInt32();
        var preimage = Stream(length);

        Assert.AreEqual(count, BlobMerkle.LeafCount(length));
        CollectionAssert.AreEqual(root, BlobMerkle.Root(preimage));

        foreach (var entry in pinned.GetProperty("paths").EnumerateArray())
        {
            var index = entry.GetProperty("leaf_index").GetInt32();
            var offset = entry.GetProperty("chunk_offset").GetInt32();
            var chunkLength = entry.GetProperty("chunk_length").GetInt32();
            var path = entry.GetProperty("path")
                .EnumerateArray()
                .Select(value => (ReadOnlyMemory<byte>)Convert.FromHexString(value.GetString()!))
                .ToList();

            Assert.AreEqual(BlobMerkle.LeafOffset(index), offset);
            Assert.AreEqual(BlobMerkle.LeafLength(length, index), chunkLength);

            var built = BlobMerkle.AuthenticationPath(preimage, index)
                .Select(step => Convert.ToHexStringLower(step))
                .ToList();
            CollectionAssert.AreEqual(
                entry.GetProperty("path").EnumerateArray().Select(value => value.GetString()!).ToList(),
                built,
                $"leaf {index}'s path");

            Assert.IsTrue(
                BlobMerkle.VerifyLeaf(root, length, index, preimage.AsSpan(offset, chunkLength), path),
                $"leaf {index}");

            // One flipped byte in the chunk, path untouched: the path is not
            // the proof, and a destination that kept only the path cannot
            // answer with it.
            var tampered = preimage.AsSpan(offset, chunkLength).ToArray();
            tampered[^1] ^= 0x01;
            Assert.IsFalse(BlobMerkle.VerifyLeaf(root, length, index, tampered, path), $"leaf {index} tampered");
        }
    }

    [TestMethod]
    public void Merkle_EveryWholePreimage_ReproducesTheStatedRoot()
    {
        foreach (var entry in Vectors.RootElement.GetProperty("whole_preimages")
                     .GetProperty("cases").EnumerateArray())
        {
            var length = entry.GetProperty("preimage_length").GetInt32();
            var preimage = Stream(length);

            Assert.AreEqual(
                entry.GetProperty("leaf_count").GetInt32(), BlobMerkle.LeafCount(length),
                entry.GetProperty("name").GetString());
            Assert.AreEqual(
                entry.GetProperty("root").GetString(),
                Convert.ToHexStringLower(BlobMerkle.Root(preimage)),
                entry.GetProperty("name").GetString());
        }
    }
}
