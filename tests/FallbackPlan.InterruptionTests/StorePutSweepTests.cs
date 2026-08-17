using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The in-step rows of the 04 §5.1 matrix, single-stream path: the step
/// boundaries are five to nine specific moments, but a store can die at
/// EVERY put, and each budget here kills a different one — mid-upload,
/// between an extension and its blob, between delta and snapshot. The
/// universal claims hold at all of them: nothing durable is collectable, no
/// partial snapshot can exist, and a fresh process completes the job with
/// no repair or operator action (NFR-REL-001).
/// </summary>
/// <remarks>
/// At concurrency 1 the put order is deterministic: 1 the intent; 2–7 an
/// extension then its blob, for each of three data blobs; 8–9 the metadata
/// blob's extension then its blob; 10 the delta; 11 the snapshot; 12 the
/// retirement. Budgets 1 through 11 therefore each fail a distinct put;
/// budget 12 is the completed publication other suites hold.
/// </remarks>
[TestClass]
public sealed class StorePutSweepTests : InterruptionHarness
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    public async Task Publication_TheStoreDiesAfterAnyPut_LeavesARecoverableRepository(int putBudget)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var content = BuildFile(seed: 2);
        var faulting = new FaultInjectingObjectStore(store, putBudget);

        using (var source = new MemoryStream(content))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(faulting, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
        }

        // Nothing durable is collectable: whatever subset of blobs made it,
        // each is covered by the live intent — or no blob made it at all
        // because its covering extension is what died (08 §3.1, C4).
        Assert.IsEmpty(await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000));

        // No budget can leave a partial snapshot: the object either never
        // reached the store or is complete and restorable.
        if (CountUnder("snapshots") == 1)
        {
            SequenceAssert.AreEqual(content, await RestoreSnapshotAsync(store, keys, 0xB2));
        }

        // A fresh process completes over the wreckage — resumed spool, void
        // obligations, covered blobs and all — and the result restores.
        var retried = BuildFile(seed: 3);
        using (var retry = new MemoryStream(retried))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(retry, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(retried, await RestoreSnapshotAsync(store, keys, 0xC3));
    }
}
