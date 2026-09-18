using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The format-3 sealed record key (specification 05 §2.2; FR-WOR-003): a
/// content key sealed to the repository's public key with associated data
/// naming the repository and the object, so a share follows its record into
/// any blob and refuses any other record. Establishes NFR-SEC-010 for the
/// per-record plane.
/// </summary>
[TestClass]
public sealed class SealedRecordKeyTests
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly RepositoryId OtherRepo =
        RepositoryId.FromBytes(Convert.FromHexString("ffffffffffffffffffffffffffffffff"));

    private static readonly ObjectId SomeId = ObjectId.FromBytes(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly ObjectId OtherId = ObjectId.FromBytes(Enumerable.Repeat((byte)0x22, 32).ToArray());

    [TestMethod]
    public void Seal_ThenOpenForTheSameRecord_YieldsTheKey()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var contentKey = Enumerable.Range(0, 32).Select(value => (byte)(value ^ 0x99)).ToArray();

        var share = SealedRecordKey.Seal(authority.Credential.SealingPublicKey, contentKey, Repo, SomeId);
        Assert.AreEqual(SealedRecordKey.SealedLength, share.Length);

        SequenceAssert.AreEqual(contentKey, SealedRecordKey.Open(authority.SealingPrivateKey, share, Repo, SomeId));
    }

    [TestMethod]
    public void Open_AShareSealedForAnotherObjectOrRepository_IsRefused()
    {
        // The share's associated data is the record's identity, not its
        // container: moved with its record it opens anywhere; moved onto
        // another record it opens nowhere.
        using var authority = WriteOnlyDerivation.FromRoot(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var share = SealedRecordKey.Seal(authority.Credential.SealingPublicKey, new byte[32], Repo, SomeId);

        Assert.ThrowsExactly<SealedContentException>(
            () => SealedRecordKey.Open(authority.SealingPrivateKey, share, Repo, OtherId));
        Assert.ThrowsExactly<SealedContentException>(
            () => SealedRecordKey.Open(authority.SealingPrivateKey, share, OtherRepo, SomeId));
    }

    [TestMethod]
    public void WriteAad_IsTheRepositoryThenTheObject()
    {
        var aad = new byte[SealedRecordKey.AadLength];
        SealedRecordKey.WriteAad(Repo, SomeId, aad);

        Assert.AreEqual(48, aad.Length);
        SequenceAssert.AreEqual(Repo.ToArray(), aad[..16]);
        SequenceAssert.AreEqual(SomeId.ToArray(), aad[16..]);
        Assert.ThrowsExactly<ArgumentException>(() => SealedRecordKey.WriteAad(Repo, SomeId, new byte[32]));
    }
}
