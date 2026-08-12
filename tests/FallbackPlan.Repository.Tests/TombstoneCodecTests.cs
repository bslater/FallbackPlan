using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.Tests;

/// <summary>
/// The tombstone codec (specification 11 §3): the one lifecycle object that
/// authorises, so its encoding, its signature coverage, and its width rule
/// are pinned before any collector writes one.
/// </summary>
[TestClass]
public sealed class TombstoneCodecTests
{
    private static readonly byte[] WriterId = [.. Enumerable.Repeat((byte)7, 16)];
    private static readonly byte[] Signature = [.. Enumerable.Repeat((byte)9, 64)];

    [TestMethod]
    public void RoundTrip_ARecordObjectTombstone_PreservesEveryFieldAndTheSignedPrefix()
    {
        var tombstone = new Tombstone(
            ObjectTypeCode: 0x01,
            ObjectId: new byte[32],
            TombstoneReason.Unreferenced,
            WriterId,
            TombstonedAtUnixMilliseconds: 1_700_000_000_000,
            EligibleGeneration: 42);

        var decoded = TombstoneCodec.Decode(TombstoneCodec.Encode(tombstone, Signature));

        Assert.AreEqual(tombstone.Reason, decoded.Value.Reason);
        Assert.AreEqual(42UL, decoded.Value.EligibleGeneration);
        Assert.IsTrue(decoded.Signature.Span.SequenceEqual(Signature));

        // The signed prefix rebuilt from the decode is byte-identical to the
        // encoding a signer saw — or verification would be a lottery.
        Assert.IsTrue(decoded.SignedBytes.Span.SequenceEqual(TombstoneCodec.EncodeForSigning(tombstone)));
    }

    [TestMethod]
    public void RoundTrip_ABlobTombstone_CarriesTheSixteenByteIdUnderTheReservedDomain()
    {
        var tombstone = new Tombstone(
            Tombstone.BlobTypeCode, new byte[16], TombstoneReason.Compacted, WriterId, 0, 7);

        var decoded = TombstoneCodec.Decode(TombstoneCodec.Encode(tombstone, Signature));

        Assert.AreEqual(Tombstone.BlobTypeCode, decoded.Value.ObjectTypeCode);
        Assert.HasCount(16, decoded.Value.ObjectId.ToArray());
    }

    [TestMethod]
    public void Encode_TheIdWidthDisagreesWithTheType_IsRefused()
    {
        // A 16-byte id under a 32-byte type is either damage or an attempt to
        // alias (11 §3.1) — refused at encode exactly as it would be at read.
        var wrong = new Tombstone(0x01, new byte[16], TombstoneReason.Unreferenced, WriterId, 0, 1);

        Assert.ThrowsExactly<ManifestValidationException>(() => TombstoneCodec.Encode(wrong, Signature));
    }

    [TestMethod]
    public void Encode_AReasonOutsideTheVocabulary_IsRefused()
    {
        var unknown = new Tombstone(0x01, new byte[32], (TombstoneReason)9, WriterId, 0, 1);

        Assert.ThrowsExactly<ManifestValidationException>(() => TombstoneCodec.Encode(unknown, Signature));
    }

    [TestMethod]
    public void Encode_ASignatureOfTheWrongLength_IsRefused()
    {
        var tombstone = new Tombstone(0x01, new byte[32], TombstoneReason.Unreferenced, WriterId, 0, 1);

        Assert.ThrowsExactly<ManifestValidationException>(() => TombstoneCodec.Encode(tombstone, new byte[63]));
    }
}
