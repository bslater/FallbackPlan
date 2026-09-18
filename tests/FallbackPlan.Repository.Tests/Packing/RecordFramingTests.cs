using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.Packing;

/// <summary>
/// The record prefix by container (specification 04 §2): nothing in format
/// 2, a carried nonce in every format-3 record, and the sealed record key
/// after it in a format-3 data record. One arithmetic for every length site.
/// </summary>
[TestClass]
public sealed class RecordFramingTests
{
    [TestMethod]
    public void PrefixLength_IsByVersionAndClass()
    {
        Assert.AreEqual(0, RecordFraming.PrefixLength(FormatVersions.Symmetric, BlobClass.Metadata));
        Assert.AreEqual(0, RecordFraming.PrefixLength(FormatVersions.SealedDataPlane, BlobClass.Data));
        Assert.AreEqual(12, RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Metadata));
        Assert.AreEqual(92, RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Data));
        Assert.AreEqual(RecordNonce.AesGcmLength, RecordFraming.SealedKeyOffset);
    }

    [TestMethod]
    public void RecordLength_IncludesThePrefixAndTheTag()
    {
        Assert.AreEqual(54 + 1000 + 16, RecordFraming.RecordLength(FormatVersions.SealedDataPlane, BlobClass.Data, 1000));
        Assert.AreEqual(54 + 12 + 1000 + 16, RecordFraming.RecordLength(FormatVersions.RelocatableRecords, BlobClass.Metadata, 1000));
        Assert.AreEqual(
            RecordHeader.Length + 92 + 1000 + RecordCipher.TagLength,
            RecordFraming.RecordLength(FormatVersions.RelocatableRecords, BlobClass.Data, 1000));
    }
}
