using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The format-3 associated data (specification 04 §4; NFR-SEC-003): the
/// format-2 layout without its trailing ordinal — 51 bytes that name the
/// repository, the container's stamp, the type and the object, and nothing
/// about where the record sits.
/// </summary>
[TestClass]
public sealed class RecordAadTests
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private static readonly ObjectId SomeId = ObjectId.FromBytes(Enumerable.Repeat((byte)0xc4, 32).ToArray());

    [TestMethod]
    public void WriteRelocatable_IsTheFormat2LayoutWithoutTheOrdinal()
    {
        var relocatable = new byte[RecordAad.RelocatableLength];
        RecordAad.WriteRelocatable(Repo, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, relocatable);

        var positional = new byte[RecordAad.Length];
        RecordAad.Write(Repo, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, 47, positional);

        Assert.AreEqual(51, relocatable.Length);
        SequenceAssert.AreEqual(positional[..51], relocatable);
        Assert.AreEqual(0x00, relocatable[16]);
        Assert.AreEqual(0x03, relocatable[17]);
        Assert.AreEqual((byte)ObjectType.SegmentRecord, relocatable[18]);
    }

    [TestMethod]
    public void WriteRelocatable_TheWrongLengthOrAPositionalVersion_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => RecordAad.WriteRelocatable(Repo, FormatVersions.RelocatableRecords, ObjectType.SegmentRecord, SomeId, new byte[55]));
        Assert.ThrowsExactly<ArgumentException>(
            () => RecordAad.WriteRelocatable(Repo, FormatVersions.SealedDataPlane, ObjectType.SegmentRecord, SomeId, new byte[51]));
    }
}
