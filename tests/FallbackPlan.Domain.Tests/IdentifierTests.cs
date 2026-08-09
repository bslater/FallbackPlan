using FallbackPlan.Domain.Identifiers;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Exercises the identifier value types' construction rules, equality, and
/// rendering (specification 02).
/// </summary>
[TestClass]
public sealed class IdentifierTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(17)]
    [DataRow(32)]
    public void Sixteen_byte_identifiers_reject_every_other_length(int length)
    {
        var bytes = new byte[length];

        Assert.ThrowsExactly<ArgumentException>(() => WriterId.FromBytes(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => RepositoryId.FromBytes(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => KeyId.FromBytes(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => BlobId.FromBytes(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => StoreBlobKey.FromBytes(bytes));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(16)]
    [DataRow(31)]
    [DataRow(33)]
    public void Thirty_two_byte_identifiers_reject_every_other_length(int length)
    {
        var bytes = new byte[length];

        Assert.ThrowsExactly<ArgumentException>(() => ContentId.FromBytes(bytes));
        Assert.ThrowsExactly<ArgumentException>(() => ObjectId.FromBytes(bytes));
    }

    [TestMethod]
    public void Round_trip_preserves_the_exact_bytes()
    {
        var sixteen = Enumerable.Range(0xa0, 16).Select(value => (byte)value).ToArray();
        var thirtyTwo = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        SequenceAssert.AreEqual(sixteen, WriterId.FromBytes(sixteen).ToArray());
        SequenceAssert.AreEqual(sixteen, RepositoryId.FromBytes(sixteen).ToArray());
        SequenceAssert.AreEqual(sixteen, KeyId.FromBytes(sixteen).ToArray());
        SequenceAssert.AreEqual(sixteen, BlobId.FromBytes(sixteen).ToArray());
        SequenceAssert.AreEqual(sixteen, StoreBlobKey.FromBytes(sixteen).ToArray());
        SequenceAssert.AreEqual(thirtyTwo, ContentId.FromBytes(thirtyTwo).ToArray());
        SequenceAssert.AreEqual(thirtyTwo, ObjectId.FromBytes(thirtyTwo).ToArray());
    }

    [TestMethod]
    public void Equality_is_by_value()
    {
        var bytes = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var other = (byte[])bytes.Clone();
        other[15] ^= 0x01;

        Assert.AreEqual(WriterId.FromBytes(bytes), WriterId.FromBytes(bytes));
        Assert.AreNotEqual(WriterId.FromBytes(bytes), WriterId.FromBytes(other));
        Assert.IsTrue(WriterId.FromBytes(bytes) == WriterId.FromBytes(bytes));
        Assert.IsTrue(WriterId.FromBytes(bytes) != WriterId.FromBytes(other));
    }

    [TestMethod]
    public void Content_identifier_rendering_is_redacted()
    {
        var contentId = ContentId.FromBytes(new byte[ContentId.Size]);

        Assert.DoesNotContain("00", contentId.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", contentId.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Rendered_identifiers_produce_the_specified_character_counts()
    {
        // Specification 02 §5: 32 bytes → 52 characters, 16 bytes → 26.
        Assert.AreEqual(52, ObjectId.FromBytes(new byte[32]).ToBase32().Length);
        Assert.AreEqual(26, StoreBlobKey.FromBytes(new byte[16]).ToBase32().Length);
        Assert.AreEqual(26, RepositoryId.FromBytes(new byte[16]).ToBase32().Length);
    }
}
