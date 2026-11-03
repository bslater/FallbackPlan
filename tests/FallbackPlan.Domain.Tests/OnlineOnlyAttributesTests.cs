namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Which Windows attribute bits say a file's content is not on this device,
/// so that reading it asks a cloud provider to fetch it (ADR-0185,
/// FR-SNP-009): offline, recall on open and recall on data access. Pinned and
/// unpinned say only what the person wants kept, and a reparse point only
/// that a filter keeps the file; neither makes it online-only. None of the
/// provider's five bits is kept with a file or a folder, or named by a restore
/// as one it did not give back (ADR-0185 Amendment 1). A file pinned to the
/// device is one the person wants kept there, which no backup frees
/// (ADR-0185 Amendment 2).
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
    [DataRow(Pinned, true)]
    [DataRow(Archive | ReparsePoint | Pinned, true)]
    [DataRow(Archive | ReparsePoint | Unpinned, false)]
    [DataRow(Archive, false)]
    [DataRow(0u, false)]
    public void IsPinned_IsWhetherThePersonWantsItKeptHere(uint attributes, bool pinned) =>
        Assert.AreEqual(pinned, MetadataRules.IsPinned(attributes));

    [TestMethod]
    public void IsOnlineOnly_WithNoAttributesCaptured_IsFalse() =>
        Assert.IsFalse(MetadataRules.IsOnlineOnly(null));

    [TestMethod]
    [DataRow(Offline)]
    [DataRow(RecallOnOpen)]
    [DataRow(RecallOnDataAccess)]
    [DataRow(Pinned)]
    [DataRow(Unpinned)]
    [DataRow(Archive | ReparsePoint | Offline | RecallOnOpen | RecallOnDataAccess | Pinned | Unpinned)]
    public void UnsetFileAttributes_OfAProvidersBits_IsNone(uint captured) =>
        Assert.IsEmpty(MetadataRules.UnsetFileAttributes(captured));

    [TestMethod]
    public void WithoutProviderBits_DropsTheProvidersBitsAndKeepsTheRest()
    {
        var metadata = new EntryMetadata
        {
            ModifiedAt = 1_722_000_000_000,
            FileAttributes = Archive | ReparsePoint | Offline | RecallOnOpen | RecallOnDataAccess | Pinned | Unpinned,
        };

        var kept = MetadataRules.WithoutProviderBits(metadata);

        Assert.AreEqual(Archive | ReparsePoint, kept.FileAttributes);
        Assert.AreEqual(metadata.ModifiedAt, kept.ModifiedAt);
    }

    [TestMethod]
    public void WithoutProviderBits_OfMetadataCarryingNone_IsTheSameMetadata()
    {
        var plain = new EntryMetadata { ModifiedAt = 1_722_000_000_000, FileAttributes = Archive };
        var none = new EntryMetadata { ModifiedAt = 1_722_000_000_000 };

        Assert.AreSame(plain, MetadataRules.WithoutProviderBits(plain));
        Assert.AreSame(none, MetadataRules.WithoutProviderBits(none));
    }
}
