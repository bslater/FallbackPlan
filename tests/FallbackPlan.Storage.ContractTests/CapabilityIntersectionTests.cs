using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// What a store standing in front of several others may promise
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md) Amendment 4):
/// the weakest answer its targets give, because a caller acting on the promise
/// acts against all of them at once.
///
/// The rule exists for <c>Agent/DestinationShipSink</c>, which forwarded the
/// local metadata store's capabilities for a sink whose blob reads and writes
/// the destinations answer. That was harmless only while every destination was
/// a local filesystem promising exactly what the metadata store promises — an
/// accident of there being one provider, not a property of the design
/// (NFR-PORT-005).
///
/// Establishes NFR-PORT-005. Does not establish FR-DEST-013.
/// </summary>
[TestClass]
public sealed class CapabilityIntersectionTests
{
    private static StoreCapabilities Generous => new()
    {
        ConditionalCreate = true,
        ConditionalReplace = true,
        RangedReads = true,
        MultipartUpload = true,
        BatchDelete = true,
        ObjectVersioning = true,
        ObjectLock = true,
        ServerSideChecksums = true,
        ListingConsistency = ListingConsistency.Strong,
        MinimumStorageDuration = null,
        ArchivalTiers = false,
        MaximumObjectSize = long.MaxValue,
        MaximumMetadataBytes = 8192,
    };

    [TestMethod]
    public void Intersect_NoStores_PromisesNothingAtAll()
    {
        // Not a floor invented out of nowhere: a fan-out with no targets
        // resolved has made no statement, and saying so is the honest answer.
        Assert.IsNull(StoreCapabilities.Intersect([]));
    }

    [TestMethod]
    public void Intersect_OneStore_IsThatStore()
    {
        Assert.AreEqual(Generous, StoreCapabilities.Intersect([Generous]));
    }

    [TestMethod]
    public void Intersect_ACapabilityOneStoreLacks_IsNotPromised()
    {
        var weak = Generous with { ConditionalCreate = false, RangedReads = false };

        var result = StoreCapabilities.Intersect([Generous, weak, Generous]);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.ConditionalCreate, "a create nobody can condition was promised as conditional");
        Assert.IsFalse(result.RangedReads);
        Assert.IsTrue(result.ConditionalReplace, "a capability every store has was withdrawn");
    }

    [TestMethod]
    public void Intersect_ListingConsistency_IsTheLaggiest()
    {
        Assert.AreEqual(
            ListingConsistency.Eventual,
            StoreCapabilities.Intersect(
                [Generous, Generous with { ListingConsistency = ListingConsistency.Eventual }])!.ListingConsistency);

        // Unknown is weaker than Eventual: a store that makes no statement
        // cannot be treated as one that promises to catch up.
        Assert.AreEqual(
            ListingConsistency.Unknown,
            StoreCapabilities.Intersect(
            [
                Generous with { ListingConsistency = ListingConsistency.Eventual },
                Generous with { ListingConsistency = ListingConsistency.Unknown },
            ])!.ListingConsistency);
    }

    [TestMethod]
    public void Intersect_Sizes_AreTheSmallestAcceptedAnywhere()
    {
        var result = StoreCapabilities.Intersect(
        [
            Generous,
            Generous with { MaximumObjectSize = 5_000_000, MaximumMetadataBytes = 2048 },
            Generous with { MaximumObjectSize = 9_000_000, MaximumMetadataBytes = 4096 },
        ]);

        Assert.IsNotNull(result);
        Assert.AreEqual(5_000_000, result.MaximumObjectSize);
        Assert.AreEqual(2048, result.MaximumMetadataBytes);
    }

    [TestMethod]
    public void Intersect_MinimumStorageDuration_IsTheLongestWait()
    {
        // The one numeric member where "weakest" means larger: a caller
        // planning around early-deletion charges has to satisfy every target,
        // so the binding figure is the longest, and an absent one is no
        // constraint rather than a zero.
        var result = StoreCapabilities.Intersect(
        [
            Generous,
            Generous with { MinimumStorageDuration = TimeSpan.FromDays(30) },
            Generous with { MinimumStorageDuration = TimeSpan.FromDays(90) },
        ]);

        Assert.AreEqual(TimeSpan.FromDays(90), result!.MinimumStorageDuration);
    }

    [TestMethod]
    public void Intersect_ArchivalTiers_IsAHazardAndSurvivesOneStoreHavingIt()
    {
        // The deliberate asymmetry. Every other member narrows; this one
        // widens, because it says rehydration latency applies rather than
        // that a capability is available. Intersecting it away would have the
        // fan-out claim nothing it writes to archives while one target does.
        var result = StoreCapabilities.Intersect([Generous, Generous with { ArchivalTiers = true }]);

        Assert.IsTrue(result!.ArchivalTiers, "a destination with archival tiers was reported as having none");
    }
}
