using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The format-3 record key (specification 03 §5.4; NFR-SEC-008): derived
/// from the class key and the record's own type and identifier, so nothing
/// about the blob enters it — and the writer's seed derivation beside it,
/// from which a data record's content key is drawn before it is sealed.
/// Establishes NFR-SEC-003 for format 3.
/// </summary>
[TestClass]
public sealed class RecordKeyDeriverTests
{
    private static readonly byte[] ClassKey = Enumerable.Range(0, 32).Select(value => (byte)(value * 3)).ToArray();
    private static readonly ObjectId SomeId = ObjectId.FromBytes(Enumerable.Repeat((byte)0x5a, 32).ToArray());
    private static readonly ObjectId OtherId = ObjectId.FromBytes(Enumerable.Repeat((byte)0x5b, 32).ToArray());

    [TestMethod]
    public void Derive_IsHkdfExpandUnderTheV3LabelOverTheTypeAndTheId()
    {
        var key = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, SomeId, key);

        var info = new List<byte>("fbp/record/v3"u8.ToArray()) { (byte)ObjectType.SegmentRecord };
        info.AddRange(SomeId.ToArray());
        var expected = HKDF.Expand(HashAlgorithmName.SHA256, ClassKey, 32, [.. info]);

        SequenceAssert.AreEqual(expected, key);
    }

    [TestMethod]
    public void Derive_TheSameRecordFromAnyBlob_IsTheSameKey()
    {
        // The whole point: no salt, writer or counter in the inputs, so a
        // reader in any blob derives the key the writer sealed under.
        var one = new byte[RecordKeyDeriver.RecordKeyLength];
        var two = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, SomeId, one);
        RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, SomeId, two);

        SequenceAssert.AreEqual(one, two);
    }

    [TestMethod]
    public void Derive_AnotherIdOrAnotherType_IsAnotherKey()
    {
        var segment = new byte[RecordKeyDeriver.RecordKeyLength];
        var other = new byte[RecordKeyDeriver.RecordKeyLength];
        var tree = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, SomeId, segment);
        RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, OtherId, other);
        RecordKeyDeriver.Derive(ClassKey, ObjectType.TreeManifest, SomeId, tree);

        Assert.IsFalse(segment.SequenceEqual(other), "two objects must never share a key");
        Assert.IsFalse(segment.SequenceEqual(tree), "the object type is a derivation input");
    }

    [TestMethod]
    public void Derive_TheWrongKeyOrDestinationLength_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => RecordKeyDeriver.Derive(new byte[16], ObjectType.SegmentRecord, SomeId, new byte[32]));
        Assert.ThrowsExactly<ArgumentException>(
            () => RecordKeyDeriver.Derive(ClassKey, ObjectType.SegmentRecord, SomeId, new byte[16]));
    }

    [TestMethod]
    public void DeriveFromSeed_IsHkdfExpandUnderTheSeedLabelOverTheId()
    {
        var seed = Enumerable.Repeat((byte)0x33, 32).ToArray();
        var key = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.DeriveFromSeed(seed, SomeId, key);

        var info = new List<byte>("fbp/record-seed/v3"u8.ToArray());
        info.AddRange(SomeId.ToArray());
        SequenceAssert.AreEqual(HKDF.Expand(HashAlgorithmName.SHA256, seed, 32, [.. info]), key);

        var other = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.DeriveFromSeed(seed, OtherId, other);
        Assert.IsFalse(key.SequenceEqual(other));
    }
}
