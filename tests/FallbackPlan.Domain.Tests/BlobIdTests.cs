using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises writer-allocated blob-identifier formation (specification 02 §4)
/// against the constant pinned in the committed conformance vectors.
/// </summary>
[TestClass]
public sealed class BlobIdTests
{
    [TestMethod]
    public void Structured_formation_matches_the_committed_conformance_constant()
    {
        // identifiers.json blob_identifier group: writer_id a0a1…af, counter 42.
        var writer = WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

        var blobId = BlobId.FromWriterCounter(writer, 42);

        SequenceAssert.AreEqual("a0a1a2a3a4a5a6a7000000000000002a", Convert.ToHexStringLower(blobId.ToArray()));
    }

    [TestMethod]
    public void Structured_formation_carries_only_the_first_eight_writer_bytes()
    {
        var writerA = WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var writerB = WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7ffffffffffffffff"));

        // Same leading eight bytes, same counter — same blob identifier: the
        // trailing writer bytes are deliberately not part of the formation.
        Assert.AreEqual(BlobId.FromWriterCounter(writerA, 7), BlobId.FromWriterCounter(writerB, 7));
    }

    [TestMethod]
    public void Counter_is_encoded_big_endian()
    {
        var writer = WriterId.FromBytes(new byte[WriterId.Size]);

        var blobId = BlobId.FromWriterCounter(writer, 0x0102030405060708);

        SequenceAssert.AreEqual("00000000000000000102030405060708", Convert.ToHexStringLower(blobId.ToArray()));
    }

    [TestMethod]
    public void Random_formation_requires_exactly_sixteen_bytes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => BlobId.FromRandom(new byte[15]));
        Assert.ThrowsExactly<ArgumentException>(() => BlobId.FromRandom(new byte[17]));
    }
}
