using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The interruption matrix over a store that misbehaves the way real ones do:
/// a put that tears and leaves partial bytes under the final key, and
/// acknowledged objects that vanish when power is lost before the directory
/// entries reach the platter. The local provider's temp-plus-rename makes
/// both unreachable on its own disk — these are the states a phase-3
/// provider, or this platform lying about durability, will produce, and
/// architecture 04 §5.1's claims (NFR-REL-001) have to hold against them,
/// not only against the clean exceptions the rest of this suite injects.
/// </summary>
[TestClass]
public sealed class StoreFaultTests : InterruptionHarness
{
    public static IEnumerable<object[]> KillPoints() =>
    [
        [PublicationStep.PublishIntent],
        [PublicationStep.UploadBlobs],
        [PublicationStep.PublishIndexDeltas],
        [PublicationStep.PublishSnapshot],
        [PublicationStep.RetireIntent],
    ];

    [TestMethod]
    [DynamicData(nameof(KillPoints))]
    public async Task Publication_KilledAtAnyStepAndItsUnflushedObjectsVanish_LeavesTheCommittedSnapshotIntactAndCompletesOnRerun(
        PublicationStep killAfter)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // The committed baseline is durable — its directory entries flushed.
        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var vanishing = new VanishingObjectStore(store);
        vanishing.Checkpoint();

        // The second run dies at the step boundary, and then power is lost:
        // everything it wrote — blobs, journal records, deltas, even its
        // snapshot — vanishes, because none of its directory entries were
        // flushed. 04 §5.1's reading is that a vanished object is one that
        // was never written, so this must land in a state the plain kill
        // matrix already proves recoverable.
        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(vanishing, keys, hierarchy, new KillAfter(killAfter))
                    .PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
        }

        await vanishing.LosePowerAsync();

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));

        // A fresh process completes the job over the void the vanish left —
        // the local sequence state survived (it is not store state), so the
        // rerun allocates fresh numbers and owes void deltas for the dead
        // run's, with nothing at the old journal keys to collide with.
        using (var retry = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(retry, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    [TestMethod]
    public async Task Publication_ReportsSuccessAndThenEveryObjectVanishes_LeavesThePriorSnapshotAndTheRerunWhole()
    {
        // The dishonest acknowledgement: the run completes, the caller is told
        // committed, and then every directory entry is lost. The durability
        // report was wrong — that is the platform's documented window, not
        // the format's — but the repository must read as if the run never
        // happened: prior snapshot intact, rerun clean, no wedged state.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var vanishing = new VanishingObjectStore(store);
        vanishing.Checkpoint();

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(vanishing, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await vanishing.LosePowerAsync();

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));

        var third = BuildFile(seed: 3);
        using (var source = new MemoryStream(third))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xC3), CancellationToken.None);
        }

        SequenceAssert.AreEqual(third, await RestoreSnapshotAsync(store, keys, 0xC3));
    }

    [TestMethod]
    public async Task Publication_CompletesButOnlyItsBlobsVanish_TheReferencingSnapshotRefusesLoudlyAndThePriorRestores()
    {
        // Directory entries flush in no promised order, so a blob's entry can
        // be lost while the index delta's survives — an index that references
        // objects the store no longer holds. The promise under test is
        // loudness: the damaged snapshot must refuse, never restore wrong
        // bytes, and the undamaged one must be untouched (NFR-REL-004).
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        var vanishing = new VanishingObjectStore(store);
        vanishing.Checkpoint();

        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(vanishing, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        await vanishing.LosePowerAsync(key => key.StartsWith("blobs/", StringComparison.Ordinal));

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
        await Assert.ThrowsAsync<Exception>(async () => await RestoreSnapshotAsync(store, keys, 0xB2));
    }

    [TestMethod]
    public async Task Publication_ABlobPutTears_FailsTheJobAndTheForensicPathStillRestoresTheCommittedSnapshot()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // The first blob put of the second run tears: partial bytes become
        // visible under the final key and the writer is told the put failed.
        var tearing = new TearingObjectStore(store, tearAtBlobPut: 1);
        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(tearing, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None));
        }

        Assert.IsNotNull(tearing.TornKey, "the tear never happened — the test proved nothing");

        // Corruption is local (architecture 04 §7; NFR-REL-004): the torn
        // orphan is skipped with a named finding and every other blob loads,
        // so the committed snapshot restores through the plain path — the
        // posture the recovery tool and the verify engine already held, now
        // converged on by the plain reader (SF-1).
        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            var skipped = Assert.ContainsSingle(reader.SkippedBlobs);
            Assert.AreEqual(tearing.TornKey, skipped.Key.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(skipped.Reason));
        }

        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));

        // The forensic rebuilder is the path built for damaged stores: it
        // must scope the torn blob as a finding and still satisfy a rebuild
        // targeted at the committed snapshot.
        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "torn-forensic.db"), Repo);

        var report = await rebuilder.RebuildAsync(
            catalogue,
            new ForensicTarget.Snapshot(Enumerable.Repeat((byte)0xA1, 16).ToArray()),
            CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied, string.Join("; ", report.Findings.Select(finding => finding.Detail)));
        Assert.IsNotEmpty(report.Findings);
    }
}
