using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// Exercises the object-identifier deriver's validation and keying rules
/// (specification 02 §3; NFR-SEC-004).
/// </summary>
[TestClass]
public sealed class ObjectIdDeriverTests
{
    private static readonly ContentId SomeContent = ContentHasher.Hash("segment plaintext"u8);

    [TestMethod]
    [DataRow((byte)0x00)]
    [DataRow((byte)0x07)] // reserved store-key domain separator
    [DataRow((byte)0x10)]
    public void Unassigned_object_types_are_refused(byte value)
    {
        using var deriver = new ObjectIdDeriver(new byte[32]);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => deriver.Derive((ObjectType)value, SomeContent));
    }

    [TestMethod]
    public void The_key_is_exactly_thirty_two_bytes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ObjectIdDeriver(new byte[31]));
        Assert.ThrowsExactly<ArgumentException>(() => new ObjectIdDeriver(new byte[33]));
        Assert.ThrowsExactly<ArgumentException>(() => new StoreBlobKeyDeriver(new byte[16]));
    }

    [TestMethod]
    public void Different_keys_produce_unrelated_identifiers()
    {
        var keyA = new byte[32];
        var keyB = new byte[32];
        keyB[0] = 0x01;

        using var deriverA = new ObjectIdDeriver(keyA);
        using var deriverB = new ObjectIdDeriver(keyB);

        Assert.AreNotEqual(
            deriverA.Derive(ObjectType.SegmentRecord, SomeContent),
            deriverB.Derive(ObjectType.SegmentRecord, SomeContent));
    }

    [TestMethod]
    public void Different_object_types_produce_unrelated_identifiers()
    {
        using var deriver = new ObjectIdDeriver(new byte[32]);

        Assert.AreNotEqual(
            deriver.Derive(ObjectType.SegmentRecord, SomeContent),
            deriver.Derive(ObjectType.FileVersionManifest, SomeContent));
    }

    [TestMethod]
    public void Incremental_and_one_shot_hashing_agree()
    {
        var plaintext = Enumerable.Range(0, 100_000).Select(value => (byte)value).ToArray();

        using var hasher = new ContentHasher();
        hasher.Append(plaintext.AsSpan(0, 1));
        hasher.Append(plaintext.AsSpan(1, 99));
        hasher.Append(plaintext.AsSpan(100));

        Assert.AreEqual(ContentHasher.Hash(plaintext), hasher.FinishAndReset());
    }
}
