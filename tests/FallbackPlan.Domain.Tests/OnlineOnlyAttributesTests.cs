namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Which Windows attribute bits say a file's content is not on this device,
/// so that reading it asks a cloud provider to fetch it (ADR-0185,
/// FR-SNP-009): offline, recall on open and recall on data access. Pinned and
/// unpinned say only what the person wants kept, and a reparse point only
/// that a filter keeps the file; neither makes it online-only.
/// </summary>
[TestClass]
public sealed class OnlineOnlyAttributesTests
{
    private const uint Offline = 0x1000;
    private const uint RecallOnOpen = 0x4_0000;
    private const uint RecallOnDataAccess = 0x40_0000;
    private const uint Pinned = 0x8_0000;
    private const uint Unpinned = 0x10_0000;
    private const uint ReparsePoint = 0x400;
    private const uint Archive = 0x20;

    [TestMethod]
    [DataRow(RecallOnDataAccess)]
    [DataRow(RecallOnOpen)]
    [DataRow(Offline)]
    [DataRow(Archive | ReparsePoint | Unpinned | RecallOnDataAccess | Offline)]
    public void IsOnlineOnly_WhereABitSaysTheContentIsNotHere_IsTrue(uint attributes) =>
        Assert.IsTrue(MetadataRules.IsOnlineOnly(attributes));

    [TestMethod]
    [DataRow(0u)]
    [DataRow(Archive)]
    [DataRow(Archive | ReparsePoint)]
    [DataRow(Archive | ReparsePoint | Pinned)]
    [DataRow(Archive | ReparsePoint | Unpinned)]
    public void IsOnlineOnly_WhereNoBitSaysSo_IsFalse(uint attributes) =>
        Assert.IsFalse(MetadataRules.IsOnlineOnly(attributes));

    [TestMethod]
    public void IsOnlineOnly_WithNoAttributesCaptured_IsFalse() =>
        Assert.IsFalse(MetadataRules.IsOnlineOnly(null));
}
