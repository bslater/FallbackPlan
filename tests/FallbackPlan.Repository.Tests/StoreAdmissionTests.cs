using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests;

/// <summary>
/// What the engine requires of a store, asked before it is used rather than
/// discovered somewhere deep
/// ([ADR-0012](../../docs/adr/0012-storage-provider-contract.md)).
/// Establishes NFR-PORT-004 and NFR-COMP-005.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0012 names two capabilities as changing engine behaviour rather than
/// merely informing it, and until now nothing read either: every reader of
/// <c>Capabilities</c> in the product asked for the maximum object size. A
/// promise nothing keeps is worse than a stated incapacity, so the engine
/// states what it needs and refuses a store that has said it cannot.
/// </para>
/// <para>
/// The split between reading and writing is the load-bearing part. A reader
/// never puts, so conditional create is nothing to it — and a peer replica
/// opened over the retrieval session is exactly that store, read-only by
/// construction (07 §1). Requiring the primitive of every store would refuse
/// the adoption path for no reason at all.
/// </para>
/// </remarks>
[TestClass]
public sealed class StoreAdmissionTests
{
    private static StoreCapabilities Capable => new()
    {
        ConditionalCreate = true,
        RangedReads = true,
        ListingConsistency = ListingConsistency.Strong,
    };

    [TestMethod]
    public void AStoreWithoutConditionalCreate_IsRefusedForWriting_ByName()
    {
        var refusal = StoreAdmission.RefuseForWriting(Capable with { ConditionalCreate = false });

        Assert.IsNotNull(refusal);
        Assert.Contains("conditional create", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void AStoreWithoutConditionalCreate_IsAdmittedForReading_BecauseAReaderNeverPuts()
    {
        Assert.IsNull(StoreAdmission.RefuseForReading(Capable with { ConditionalCreate = false }));
    }

    [TestMethod]
    public void AStoreWithoutRangedReads_IsRefusedForBoth()
    {
        var withheld = Capable with { RangedReads = false };

        Assert.IsNotNull(StoreAdmission.RefuseForWriting(withheld));
        Assert.IsNotNull(StoreAdmission.RefuseForReading(withheld));
        Assert.Contains("ranged reads", StoreAdmission.RefuseForReading(withheld)!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void AStoreDeclaringBoth_IsAdmittedForBoth()
    {
        Assert.IsNull(StoreAdmission.RefuseForWriting(Capable));
        Assert.IsNull(StoreAdmission.RefuseForReading(Capable));
    }

    [TestMethod]
    public void TheOnlyRealProvider_IsAdmittedUnchanged()
    {
        // The gate must be unreachable in production until a second provider
        // exists; a refusal the local filesystem could trip would be a
        // regression wearing a check's clothes.
        var root = Path.Combine(Path.GetTempPath(), "fbp-admission", Guid.NewGuid().ToString("n"));
        try
        {
            var store = new LocalFileSystemObjectStore(root);
            Assert.IsNull(StoreAdmission.RefuseForWriting(store.Capabilities));
            Assert.IsNull(StoreAdmission.RefuseForReading(store.Capabilities));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AReadOnlyStoresProfile_PassesAReadingAdmissionAndFailsAWritingOne()
    {
        // The shape a peer's replica has over the retrieval session: ranged
        // reads and nothing else, because it has no put at all. Whether the
        // adapters declare this is asserted where they are visible
        // (Hosts.Tests/PeerStoreCapabilityTests); what is asserted here is
        // that the gate answers differently for the two questions, which is
        // the whole reason there are two.
        var readOnly = new StoreCapabilities { RangedReads = true };

        Assert.IsNull(StoreAdmission.RefuseForReading(readOnly));
        Assert.IsNotNull(
            StoreAdmission.RefuseForWriting(readOnly),
            "a read-only store must not pass a writing admission, or the gate says nothing");
    }

    [TestMethod]
    public async Task AnOpenForWriting_OverAStoreWithoutConditionalCreate_IsRefusedBeforeAnythingIsRead()
    {
        // The refusal reaches the caller as the same exception a repository
        // this reader cannot use raises, because it is the same kind of
        // answer: not damage, not a fault, a stated incapacity.
        var root = Path.Combine(Path.GetTempPath(), "fbp-admission-open", Guid.NewGuid().ToString("n"));
        try
        {
            var inner = new LocalFileSystemObjectStore(root);
            var degraded = new DegradedObjectStore(
                inner, new StoreCapabilities { ConditionalCreate = false, RangedReads = true });

            using var credential = TestAuthority.Shared.Credential.Clone();
            var refusal = await Assert.ThrowsExactlyAsync<RepositoryOpenException>(async () =>
                await RepositoryLifecycle.OpenAsync(degraded, credential, CancellationToken.None));

            Assert.Contains("conditional create", refusal.Message, StringComparison.OrdinalIgnoreCase);

            // Nothing was read: there is no repository here at all, and a
            // store the engine cannot use is refused before that matters.
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "repository-format")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
