namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The questions code asks of a format version (NFR-COMP-004, FR-MAN-015;
/// specification 00 §5; ADR-0096). Format 4 changes the index delta and
/// nothing else: it ranks entries by an index generation, and its records and
/// blobs are format 3's byte for byte.
/// </summary>
[TestClass]
public sealed class FormatVersionsTests
{
    [TestMethod]
    public void FormatFour_HasIndexGenerations_AndNoEarlierFormatDoes()
    {
        Assert.IsTrue(FormatVersions.HasIndexGenerations(FormatVersions.IndexGenerations));
        Assert.IsFalse(FormatVersions.HasIndexGenerations(FormatVersions.RelocatableRecords));
        Assert.IsFalse(FormatVersions.HasIndexGenerations(FormatVersions.SealedDataPlane));
    }

    [TestMethod]
    public void FormatFour_KeepsFormatThreesRecordsAndBlobs()
    {
        // A format-4 repository's blobs are stamped 3, because nothing in a
        // blob changed. A stamp of 4 would make every blob unreadable to a
        // format-3 reader for no reason, and the stamp is associated data.
        Assert.IsTrue(FormatVersions.HasRelocatableRecords(FormatVersions.IndexGenerations));
        Assert.AreEqual(
            FormatVersions.RelocatableRecords, FormatVersions.ContainerVersion(FormatVersions.IndexGenerations, dataClass: true));
        Assert.AreEqual(
            FormatVersions.RelocatableRecords, FormatVersions.ContainerVersion(FormatVersions.IndexGenerations, dataClass: false));
    }

    [TestMethod]
    public void ThisBuild_ReadsFormatsTwoToFour_AndNothingAfter()
    {
        Assert.IsFalse(FormatVersions.IsReadable(1));
        Assert.IsTrue(FormatVersions.IsReadable(FormatVersions.SealedDataPlane));
        Assert.IsTrue(FormatVersions.IsReadable(FormatVersions.RelocatableRecords));
        Assert.IsTrue(FormatVersions.IsReadable(FormatVersions.IndexGenerations));
        Assert.IsFalse(FormatVersions.IsReadable(5));
        Assert.AreEqual((ushort)4, FormatLimits.LatestFormatVersion);
    }
}
