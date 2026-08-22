using System.Reflection;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The value semantics every identifier, profile and sized quantity in the
/// domain depends on. These types are dictionary keys and set members
/// throughout the engine — the catalogue, the index, dedup — so an
/// <see cref="object.Equals(object)"/> that answers true for the wrong type,
/// or a <see cref="object.GetHashCode"/> inconsistent with equality, is a
/// data-integrity defect rather than a cosmetic one.
/// </summary>
/// <remarks>
/// The contract is stated once and applied to every type, so a type added
/// later inherits the same scrutiny by being added to one list, and no type
/// gets a weaker check because its bespoke test was written in a hurry.
/// Operators are invoked through reflection because C# cannot call them
/// generically; a type that declares one gets it exercised, and a type that
/// does not is simply not asked.
/// </remarks>
[TestClass]
public sealed class ValueSemanticsTests
{
    private static byte[] Pattern(int size, byte seed) =>
        [.. Enumerable.Range(0, size).Select(index => (byte)(index + seed))];

    /// <summary>
    /// Asserts the full contract for a value type, given two equal instances
    /// and one that differs.
    /// </summary>
    private static void AssertValueSemantics<T>(T value, T equal, T different)
        where T : notnull
    {
        // Equality: reflexive, symmetric, and false for a different value.
        Assert.IsTrue(value.Equals(equal), $"{typeof(T).Name}: equal instances compared unequal.");
        Assert.IsTrue(equal.Equals(value), $"{typeof(T).Name}: equality is not symmetric.");
        Assert.IsFalse(value.Equals(different), $"{typeof(T).Name}: different instances compared equal.");

        // The object overload must agree, and must refuse null and foreign
        // types rather than throwing or answering true.
        Assert.IsTrue(value.Equals((object)equal));
        Assert.IsFalse(value.Equals((object)different));
        Assert.IsFalse(value.Equals(null));
        Assert.IsFalse(value.Equals("not a " + typeof(T).Name));

        // Hashing must agree with equality. The converse is NOT asserted:
        // distinct values are permitted to collide, and a test that forbids
        // it would be testing luck.
        Assert.AreEqual(value.GetHashCode(), equal.GetHashCode());

        Assert.IsFalse(string.IsNullOrWhiteSpace(value.ToString()), $"{typeof(T).Name}: ToString is empty.");

        AssertOperators(value, equal, different);
    }

    private static void AssertOperators<T>(T value, T equal, T different)
        where T : notnull
    {
        Assert.IsTrue(Invoke("op_Equality", value, equal) ?? true);
        Assert.IsFalse(Invoke("op_Equality", value, different) ?? false);
        Assert.IsFalse(Invoke("op_Inequality", value, equal) ?? false);
        Assert.IsTrue(Invoke("op_Inequality", value, different) ?? true);

        if (value is not IComparable<T> comparable)
        {
            return;
        }

        // Ordering, where declared, must agree with CompareTo — the usual
        // failure is an operator that quietly inverts.
        var ordersBefore = comparable.CompareTo(different) < 0;
        Assert.AreEqual(ordersBefore, Invoke("op_LessThan", value, different) ?? ordersBefore);
        Assert.AreEqual(!ordersBefore, Invoke("op_GreaterThan", value, different) ?? !ordersBefore);
        Assert.AreEqual(ordersBefore, Invoke("op_LessThanOrEqual", value, different) ?? ordersBefore);
        Assert.AreEqual(!ordersBefore, Invoke("op_GreaterThanOrEqual", value, different) ?? !ordersBefore);
        Assert.IsTrue(Invoke("op_LessThanOrEqual", value, equal) ?? true);
        Assert.IsTrue(Invoke("op_GreaterThanOrEqual", value, equal) ?? true);
        Assert.AreEqual(0, comparable.CompareTo(equal));
    }

    /// <summary>Invokes a declared operator, or returns null when the type declares none.</summary>
    private static bool? Invoke<T>(string name, T left, T right)
    {
        var method = typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        return method is null ? null : (bool)method.Invoke(null, [left, right])!;
    }

    [TestMethod]
    public void Identifiers_ComparedAndHashed_HonourTheValueContract()
    {
        AssertValueSemantics(
            ObjectId.FromBytes(Pattern(ObjectId.Size, 1)),
            ObjectId.FromBytes(Pattern(ObjectId.Size, 1)),
            ObjectId.FromBytes(Pattern(ObjectId.Size, 2)));

        AssertValueSemantics(
            ContentId.FromBytes(Pattern(ContentId.Size, 1)),
            ContentId.FromBytes(Pattern(ContentId.Size, 1)),
            ContentId.FromBytes(Pattern(ContentId.Size, 2)));

        AssertValueSemantics(
            BlobId.FromBytes(Pattern(BlobId.Size, 1)),
            BlobId.FromBytes(Pattern(BlobId.Size, 1)),
            BlobId.FromBytes(Pattern(BlobId.Size, 2)));

        AssertValueSemantics(
            KeyId.FromBytes(Pattern(KeyId.Size, 1)),
            KeyId.FromBytes(Pattern(KeyId.Size, 1)),
            KeyId.FromBytes(Pattern(KeyId.Size, 2)));

        AssertValueSemantics(
            RepositoryId.FromBytes(Pattern(RepositoryId.Size, 1)),
            RepositoryId.FromBytes(Pattern(RepositoryId.Size, 1)),
            RepositoryId.FromBytes(Pattern(RepositoryId.Size, 2)));

        AssertValueSemantics(
            WriterId.FromBytes(Pattern(WriterId.Size, 1)),
            WriterId.FromBytes(Pattern(WriterId.Size, 1)),
            WriterId.FromBytes(Pattern(WriterId.Size, 2)));

        AssertValueSemantics(
            StoreBlobKey.FromBytes(Pattern(StoreBlobKey.Size, 1)),
            StoreBlobKey.FromBytes(Pattern(StoreBlobKey.Size, 1)),
            StoreBlobKey.FromBytes(Pattern(StoreBlobKey.Size, 2)));

        AssertValueSemantics(
            CheckpointId.FromBytes(Pattern(CheckpointId.Size, 1)),
            CheckpointId.FromBytes(Pattern(CheckpointId.Size, 1)),
            CheckpointId.FromBytes(Pattern(CheckpointId.Size, 2)));

        AssertValueSemantics(
            DeltaId.FromBytes(Pattern(DeltaId.Size, 1)),
            DeltaId.FromBytes(Pattern(DeltaId.Size, 1)),
            DeltaId.FromBytes(Pattern(DeltaId.Size, 2)));
    }

    [TestMethod]
    public void SizedQuantities_ComparedAndHashed_HonourTheValueContract()
    {
        AssertValueSemantics(new KeyGeneration(7), new KeyGeneration(7), new KeyGeneration(9));
        AssertValueSemantics(SegmentSize.Create(65_536), SegmentSize.Create(65_536), SegmentSize.Create(131_072));
        AssertValueSemantics(
            CdcParameters.Create(1024 * 1024, 256 * 1024, 8 * 1024 * 1024),
            CdcParameters.Create(1024 * 1024, 256 * 1024, 8 * 1024 * 1024),
            CdcParameters.Create(512 * 1024, 128 * 1024, 4 * 1024 * 1024));
    }

    /// <summary>
    /// The redaction wrappers are value types too, and they earn the same
    /// scrutiny for a sharper reason: they are compared and hashed while
    /// deciding whether two log records concern the same file or the same
    /// snapshot. An equality that answers wrongly there does not corrupt data —
    /// it tells an operator that one file failed twice when two failed once,
    /// which is worse than saying nothing.
    /// </summary>
    [TestMethod]
    public void RedactionWrappers_ComparedAndHashed_HonourTheValueContract()
    {
        AssertValueSemantics(
            LogId.Snapshot(Pattern(16, 1)),
            LogId.Snapshot(Pattern(16, 1)),
            LogId.Snapshot(Pattern(16, 2)));

        AssertValueSemantics(
            LogPath.FromString("/home/someone/tax return.pdf"),
            LogPath.FromString("/home/someone/tax return.pdf"),
            LogPath.FromString("/home/someone/other.pdf"));
    }

    [TestMethod]
    public void Profiles_ComparedAndHashed_HonourTheValueContract()
    {
        AssertValueSemantics(SegmentationProfile.FixedV1, SegmentationProfile.FixedV1, SegmentationProfile.CdcV1);
        AssertValueSemantics(CompressionProfile.None, CompressionProfile.None, CompressionProfile.ZstdV1);
        AssertValueSemantics(CompressionProfile.None, CompressionProfile.None, CompressionProfile.ZstdV1);
    }

    [TestMethod]
    public void SingleValuedProfiles_ComparedToNullOrAForeignType_RefuseEquality()
    {
        // KdfProfile and ContentHashProfile have one assigned value each, so
        // there is no "different" instance to compare against — the half of
        // the contract that still applies is that they refuse everything
        // they are not.
        Assert.IsFalse(KdfProfile.Argon2id.Equals(null));
        Assert.IsFalse(KdfProfile.Argon2id.Equals("Argon2id"));
        Assert.IsTrue(KdfProfile.Argon2id.Equals((object)KdfProfile.Argon2id));
        Assert.AreEqual(KdfProfile.Argon2id.GetHashCode(), KdfProfile.Argon2id.GetHashCode());
        Assert.AreEqual(KdfProfile.Argon2id.Name, KdfProfile.Argon2id.ToString());

        Assert.IsFalse(ContentHashProfile.Sha256V1.Equals(null));
        Assert.IsFalse(ContentHashProfile.Sha256V1.Equals("sha256-v1"));
        Assert.IsTrue(ContentHashProfile.Sha256V1.Equals((object)ContentHashProfile.Sha256V1));
        Assert.AreEqual(ContentHashProfile.Sha256V1.GetHashCode(), ContentHashProfile.Sha256V1.GetHashCode());
        Assert.AreEqual(ContentHashProfile.Sha256V1.Name, ContentHashProfile.Sha256V1.ToString());
    }

    [TestMethod]
    [DataRow(0x0000)]  // unassigned
    [DataRow(0x0002)]  // unassigned
    [DataRow(0x8000)]  // private-use range: never portable (specification 00 §3)
    [DataRow(0xFFFF)]
    public void TryFromValue_ProfileValueIsUnassigned_ReturnsFalseAndNoProfile(int value)
    {
        Assert.IsFalse(KdfProfile.TryFromValue((ushort)value, out var kdf));
        Assert.IsNull(kdf);

        Assert.IsFalse(ContentHashProfile.TryFromValue((ushort)value, out var hash));
        Assert.IsNull(hash);
    }

    [TestMethod]
    public void Profiles_EveryAssignedValue_Resolves()
    {
        Assert.IsTrue(KdfProfile.TryFromValue(KdfProfile.Argon2id.Value, out var kdf));
        Assert.AreEqual(KdfProfile.Argon2id, kdf);

        Assert.IsTrue(ContentHashProfile.TryFromValue(ContentHashProfile.Sha256V1.Value, out var hash));
        Assert.AreEqual(ContentHashProfile.Sha256V1, hash);
    }
}
