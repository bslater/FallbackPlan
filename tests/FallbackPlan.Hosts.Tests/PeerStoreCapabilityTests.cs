using FallbackPlan.Agent;
using FallbackPlan.Domain;
using FallbackPlan.Repository;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The peer adapters declare what they actually do
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md);
/// [ADR-0058](../../docs/adr/0058-peer-write-adapter.md)). Establishes
/// NFR-PORT-004.
/// </summary>
/// <remarks>
/// Both are <see cref="FallbackPlan.Storage.Abstractions.IObjectStore"/>
/// implementations that are not storage backends, and until the engine read
/// a capability neither declaration cost anything to get wrong. The write
/// adapter's did: it implements if-absent puts — <c>Holds</c> is the check —
/// and declared otherwise, which the admission gate would have refused it for.
/// </remarks>
[TestClass]
public sealed class PeerStoreCapabilityTests
{
    [TestMethod]
    public void TheWriteAdapter_DeclaresTheConditionalCreateItImplements()
    {
        Assert.IsTrue(PeerShipStore.DeclaredCapabilities.ConditionalCreate);
        Assert.IsNull(StoreAdmission.RefuseForWriting(PeerShipStore.DeclaredCapabilities));
    }

    [TestMethod]
    public void BothAdapters_DeclareASizeThatAdmitsWhatTheFormatWrites()
    {
        // Both said zero, by leaving the member at its struct default, and
        // both said it invisibly: nothing read the figure while the ship sink
        // forwarded the local metadata store's capabilities rather than its
        // destinations'. The moment it stopped, every direct-ship run to a
        // peer refused itself with "blob_maximum_exceeds_provider_object_size"
        // — the capture validating its policy against a ceiling of nought.
        //
        // The peer wire sets no ceiling of its own, so the honest figure is
        // the same one a local path gives.
        Assert.IsGreaterThanOrEqualTo(
            FormatLimits.MaxBlobSize, PeerShipStore.DeclaredCapabilities.MaximumObjectSize);
        Assert.IsGreaterThanOrEqualTo(
            FormatLimits.MaxBlobSize, PeerRetrievalObjectStore.DeclaredCapabilities.MaximumObjectSize);
    }

    [TestMethod]
    public void TheRetrievalAdapter_DeclaresNoConditionalCreate_BecauseItHasNoPutAtAll()
    {
        // Honest rather than generous: a peer replica is read-only over the
        // retrieval session (peer-protocol 07 §1), so it is admitted for
        // reading — which is what adoption and restore ask of it — and
        // refused for writing, which nothing asks.
        Assert.IsFalse(PeerRetrievalObjectStore.DeclaredCapabilities.ConditionalCreate);
        Assert.IsNull(StoreAdmission.RefuseForReading(PeerRetrievalObjectStore.DeclaredCapabilities));
        Assert.IsNotNull(StoreAdmission.RefuseForWriting(PeerRetrievalObjectStore.DeclaredCapabilities));
    }
}
